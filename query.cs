using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(
    typeof(Amazon.Lambda.Serialization.SystemTextJson
                .DefaultLambdaJsonSerializer))]

namespace GenericDdbScanLambda
{
    public class Function
    {
        private const string TableName = "AuditLogs";
        private static readonly IAmazonDynamoDB _ddb = new AmazonDynamoDBClient();
        
        // Common fields we want to include in the projection
        private static readonly string[] ProjectionFields = 
        {
            "audit_id",
            "timestamp",
            "system_id",
            "system_name",
            "user_id",
            "resource_type",
            "resource_id",
            "action_type",
            "action_description",
            "data_before",
            "data_after"
        };

        public async Task<APIGatewayHttpApiV2ProxyResponse> Handler(
            APIGatewayHttpApiV2ProxyRequest req, ILambdaContext context)
        {
            try
            {
                var filters = GatherParameters(req);

                // Build query - no required fields
                var query = new ScanRequest
                {
                    TableName = TableName,
                    ProjectionExpression = string.Join(",", ProjectionFields)
                };

                // Add any provided filters
                var filterExpressions = new List<string>();
                query.ExpressionAttributeValues = new Dictionary<string, AttributeValue>();
                var attrCount = 0;

                foreach (var filter in filters)
                {
                    if (filter.Key == "startDate" || filter.Key == "endDate") continue;
                    
                    var attrName = $":val{attrCount++}";
                    filterExpressions.Add($"{filter.Key} = {attrName}");
                    query.ExpressionAttributeValues[attrName] = new AttributeValue { S = filter.Value };
                }

                // Add timestamp range if provided
                if (filters.TryGetValue("startDate", out var startDate) &&
                    filters.TryGetValue("endDate", out var endDate))
                {
                    filterExpressions.Add("#timestamp BETWEEN :startDate AND :endDate");
                    query.ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#timestamp"] = "timestamp"
                    };
                    query.ExpressionAttributeValues[":startDate"] = new AttributeValue { S = startDate };
                    query.ExpressionAttributeValues[":endDate"] = new AttributeValue { S = endDate };
                }

                if (filterExpressions.Any())
                {
                    query.FilterExpression = string.Join(" AND ", filterExpressions);
                }

                // Handle pagination
                if (req.QueryStringParameters?.TryGetValue("nextToken", out var paginationToken) == true 
                    && !string.IsNullOrEmpty(paginationToken))
                {
                    query.ExclusiveStartKey = new Dictionary<string, AttributeValue>
                    {
                        ["PK_SystemDate"] = new AttributeValue { S = paginationToken },
                        ["SK_AuditDetails"] = new AttributeValue { S = paginationToken }
                    };
                }

                // Execute scan
                var response = await _ddb.ScanAsync(query);

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


                return new APIGatewayHttpApiV2ProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Body = JsonSerializer.Serialize(responseBody),
                    Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
                };
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error in query: {ex}");
                return Error("Error processing request", HttpStatusCode.InternalServerError);
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
                    if (!string.IsNullOrEmpty(kv.Value))
                    {
                        dict[kv.Key] = kv.Value.Trim();
                    }
                }
            }


            // JSON body (overwrites query string parameters)
            if (!string.IsNullOrEmpty(req.Body))
            {
                try
                {
                    var body = JsonSerializer.Deserialize<Dictionary<string, string>>(req.Body);
                    if (body != null)
                    {
                        foreach (var kv in body)
                        {
                            if (!string.IsNullOrEmpty(kv.Value))
                            {
                                dict[kv.Key] = kv.Value.Trim();
                            }
                        }
                    }
                }
                catch { /* Ignore JSON parse errors */ }
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
    }
}