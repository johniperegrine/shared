using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using System.Globalization;

[assembly: LambdaSerializer(
    typeof(Amazon.Lambda.Serialization.SystemTextJson
                .DefaultLambdaJsonSerializer))]

namespace GenericDdbScanLambda
{
    public class Function : IDisposable
    {
        private const string TableName = "AuditLogs";
        private readonly IAmazonDynamoDB _ddb;
        private static readonly string[] MetadataFields = { "PK_SystemDate", "SK_AuditDetails", "retention_years", "deletion_date" };
        private bool _disposed = false;

        public Function() : this(new AmazonDynamoDBClient()) { }

        // Constructor for testing with mock client
        public Function(IAmazonDynamoDB ddbClient)
        {
            _ddb = ddbClient ?? throw new ArgumentNullException(nameof(ddbClient));
        }

        public async Task<APIGatewayHttpApiV2ProxyResponse> Handler(
            APIGatewayHttpApiV2ProxyRequest req, ILambdaContext context)
        {
            context.Logger.LogInformation($"Request received: {JsonSerializer.Serialize(req)}");

            try
            {
                var filters = GatherParameters(req);

                // Validate required parameters
                if (!filters.TryGetValue("user_id", out var userId) || string.IsNullOrEmpty(userId))
                {
                    context.Logger.LogWarning("Missing required parameter: user_id");
                    return Error("user_id is required", HttpStatusCode.BadRequest);
                }

                // Build query using GSI for better performance
                var query = new QueryRequest
                {
                    TableName = TableName,
                    IndexName = "user-timestamp-index",
                    KeyConditionExpression = "user_id = :userId",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":userId"] = new AttributeValue { S = userId }
                    },
                    // Get all attributes except metadata
                    ProjectionExpression = string.Join(",",
                        typeof(AuditLogItem).GetProperties()
                            .Where(p => !MetadataFields.Contains(p.Name))
                            .Select(p => p.GetCustomAttributes(typeof(JsonPropertyNameAttribute), true)
                                .Cast<JsonPropertyNameAttribute>()
                                .FirstOrDefault()?.Name ?? p.Name))
                };

                // Add timestamp range filter if provided
                if (filters.TryGetValue("startDate", out var startDate) &&
                    filters.TryGetValue("endDate", out var endDate))
                {
                    query.KeyConditionExpression += " AND #timestamp BETWEEN :startDate AND :endDate";
                    query.ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#timestamp"] = "timestamp"
                    };
                    query.ExpressionAttributeValues[":startDate"] = new AttributeValue { S = startDate };
                    query.ExpressionAttributeValues[":endDate"] = new AttributeValue { S = endDate };
                }

                // Handle pagination
                var paginationToken = req.QueryStringParameters?.ContainsKey("nextToken") == true
                    ? req.QueryStringParameters["nextToken"]
                    : null;

                if (!string.IsNullOrEmpty(paginationToken))
                {
                    query.ExclusiveStartKey = new Dictionary<string, AttributeValue>
                    {
                        ["PK_SystemDate"] = new AttributeValue { S = paginationToken },
                        ["SK_AuditDetails"] = new AttributeValue { S = paginationToken }
                    };
                }

                // Execute query
                context.Logger.LogInformation("Executing DynamoDB query");
                var response = await _ddb.QueryAsync(query);

                // Convert to response format
                var items = response.Items
                    .Select(item => item.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.S ?? kv.Value.N ?? (kv.Value.M != null ? JsonSerializer.Serialize(kv.Value.M) : null)))
                    .ToList();

                // Build response with pagination token if there are more items
                var responseBody = new Dictionary<string, object>
                {
                    ["items"] = items
                };

                if (response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0)
                {
                    responseBody["nextToken"] = response.LastEvaluatedKey["PK_SystemDate"].S;
                }

                context.Logger.LogInformation($"Query successful. Found {items.Count} items");
                return new APIGatewayHttpApiV2ProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Body = JsonSerializer.Serialize(responseBody),
                    Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
                };
            }
            catch (Exception ex)
            {
                context.Logger.LogError(ex, "Error executing query");
                return Error("Error executing query: " + ex.Message, HttpStatusCode.InternalServerError);
            }
        }

        private static Dictionary<string, string> GatherParameters(APIGatewayHttpApiV2ProxyRequest req)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Query string parameters
            if (req.QueryStringParameters != null)
            {
                foreach (var kv in req.QueryStringParameters)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value))
                    {
                        dict[kv.Key] = kv.Value.Trim();
                    }
                }
            }

            // JSON body (overwrites query string parameters)
            if (!string.IsNullOrWhiteSpace(req.Body))
            {
                try
                {
                    var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(req.Body);
                    if (body != null)
                    {
                        foreach (var kv in body)
                        {
                            // Only process string values (ignore nested objects/arrays)
                            if (kv.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(kv.Value.GetString()))
                            {
                                dict[kv.Key] = kv.Value.GetString().Trim();
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    // Log the error but continue with query string parameters
                    LambdaLogger.Log($"Error parsing JSON body: {ex.Message}");
                }
            }

            // Ensure timestamp formats are valid
            if (dict.TryGetValue("startDate", out var startDate) &&
                !DateTime.TryParse(startDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out _))
            {
                dict.Remove("startDate");
            }

            if (dict.TryGetValue("endDate", out var endDate) &&
                !DateTime.TryParse(endDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out _))
            {
                dict.Remove("endDate");
            }

            return dict;
        }

        private static APIGatewayHttpApiV2ProxyResponse Error(string msg, HttpStatusCode code)
            => new()
            {
                StatusCode = (int)code,
                Body = JsonSerializer.Serialize(new { error = msg }),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _ddb?.Dispose();
                }
                _disposed = true;
            }
        }
    }

    public class AuditLogItem
    {
        [JsonPropertyName("audit_id")]
        public string AuditId { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }

        [JsonPropertyName("system_id")]
        public string SystemId { get; set; }

        [JsonPropertyName("system_name")]
        public string SystemName { get; set; }

        [JsonPropertyName("user_id")]
        public string UserId { get; set; }

        [JsonPropertyName("resource_type")]
        public string ResourceType { get; set; }

        [JsonPropertyName("resource_id")]
        public string ResourceId { get; set; }

        [JsonPropertyName("action_type")]
        public string ActionType { get; set; }

        [JsonPropertyName("action_description")]
        public string ActionDescription { get; set; }

        [JsonPropertyName("data_before")]
        public string DataBefore { get; set; }

        [JsonPropertyName("data_after")]
        public string DataAfter { get; set; }
    }
}