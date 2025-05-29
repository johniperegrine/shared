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

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace GenericDynamoQuery
{
    public class Function
    {
        private static readonly IAmazonDynamoDB _dynamoDb = new AmazonDynamoDBClient();

        public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            try
            {
                // 1. Get and validate table name
                if (!TryGetTableName(request, out var tableName))
                {
                    return CreateResponse("TableName is required", HttpStatusCode.BadRequest);
                }

                // 2. Get query parameters
                var (parameters, nextToken, limit) = ParseRequest(request);

                // 3. Build scan request
                var scanRequest = new ScanRequest
                {
                    TableName = tableName,
                    Limit = limit,
                    ConsistentRead = parameters.ContainsKey("consistentRead")
                };

                // 4. Add pagination token if provided
                if (!string.IsNullOrEmpty(nextToken))
                {
                    try
                    {
                        scanRequest.ExclusiveStartKey = JsonSerializer.Deserialize<Dictionary<string, AttributeValue>>(
                            Convert.FromBase64String(nextToken));
                    }
                    catch
                    {
                        return CreateResponse("Invalid nextToken", HttpStatusCode.BadRequest);
                    }
                }


                // 5. Add filters if any
                if (parameters.Count > 0)
                {
                    var filters = new List<string>();
                    var attributeNames = new Dictionary<string, string>();
                    var attributeValues = new Dictionary<string, AttributeValue>();
                    
                    int i = 0;
                    foreach (var param in parameters)
                    {
                        if (param.Key.Equals("tableName", StringComparison.OrdinalIgnoreCase) ||
                            param.Key.Equals("nextToken", StringComparison.OrdinalIgnoreCase) ||
                            param.Key.Equals("limit", StringComparison.OrdinalIgnoreCase) ||
                            param.Key.Equals("consistentRead", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var attrName = $"#attr{i}";
                        var attrValue = $":val{i}";
                        
                        attributeNames[attrName] = param.Key;
                        attributeValues[attrValue] = new AttributeValue { S = param.Value };
                        filters.Add($"{attrName} = {attrValue}");
                        
                        i++;
                    }

                    if (filters.Count > 0)
                    {
                        scanRequest.FilterExpression = string.Join(" AND ", filters);
                        scanRequest.ExpressionAttributeNames = attributeNames;
                        scanRequest.ExpressionAttributeValues = attributeValues;
                    }
                }

                // 6. Execute the scan
                var response = await _dynamoDb.ScanAsync(scanRequest);

                // 7. Format the response
                var result = new Dictionary<string, object>
                {
                    ["items"] = response.Items
                        .Select(item => item.ToDictionary(
                            kv => kv.Key,
                            kv => ConvertAttributeValue(kv.Value)))
                        .ToList()
                };

                // 8. Add pagination token if there are more items
                if (response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0)
                {
                    result["nextToken"] = Convert.ToBase64String(
                        JsonSerializer.SerializeToUtf8Bytes(response.LastEvaluatedKey));
                }

                return new APIGatewayHttpApiV2ProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Body = JsonSerializer.Serialize(result),
                    Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
                };
            }
            catch (ResourceNotFoundException)
            {
                return CreateResponse("Table not found", HttpStatusCode.NotFound);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error: {ex}");
                return CreateResponse("Internal server error", HttpStatusCode.InternalServerError);
            }
        }

        private static bool TryGetTableName(APIGatewayHttpApiV2ProxyRequest request, out string tableName)
        {
            // Try to get from query string first
            if (request.QueryStringParameters != null && 
                request.QueryStringParameters.TryGetValue("tableName", out tableName))
            {
                return !string.IsNullOrWhiteSpace(tableName);
            }

            // Then try to get from request body
            if (!string.IsNullOrEmpty(request.Body))
            {
                try
                {
                    var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Body);
                    if (body != null && body.TryGetValue("tableName", out var tableNameElement) &&
                        tableNameElement.ValueKind == JsonValueKind.String)
                    {
                        tableName = tableNameElement.GetString();
                        return !string.IsNullOrWhiteSpace(tableName);
                    }
                }
                catch (JsonException)
                {
                    // Ignore JSON parse errors
                }
            }

            tableName = null;
            return false;
        }

        private static (Dictionary<string, string> parameters, string nextToken, int? limit) 
            ParseRequest(APIGatewayHttpApiV2ProxyRequest request)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string nextToken = null;
            int? limit = null;

            // Process query string parameters
            if (request.QueryStringParameters != null)
            {
                foreach (var param in request.QueryStringParameters)
                {
                    if (param.Key.Equals("nextToken", StringComparison.OrdinalIgnoreCase))
                    {
                        nextToken = param.Value;
                    }
                    else if (param.Key.Equals("limit", StringComparison.OrdinalIgnoreCase) && 
                             int.TryParse(param.Value, out var limitValue) && limitValue > 0)
                    {
                        limit = Math.Min(limitValue, 1000); // Cap at 1000 items
                    }
                    else if (!string.IsNullOrWhiteSpace(param.Value))
                    {
                        parameters[param.Key] = param.Value.Trim();
                    }
                }
            }

            // Process request body (overwrites query string parameters)
            if (!string.IsNullOrEmpty(request.Body))
            {
                try
                {
                    var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Body);
                    if (body != null)
                    {
                        foreach (var param in body)
                        {
                            if (param.Key.Equals("nextToken", StringComparison.OrdinalIgnoreCase) && 
                                param.Value.ValueKind == JsonValueKind.String)
                            {
                                nextToken = param.Value.GetString();
                            }
                            else if (param.Key.Equals("limit", StringComparison.OrdinalIgnoreCase) && 
                                     param.Value.ValueKind == JsonValueKind.Number &&
                                     param.Value.TryGetInt32(out var limitValue) && limitValue > 0)
                            {
                                limit = Math.Min(limitValue, 1000);
                            }
                            else if (param.Value.ValueKind == JsonValueKind.String && 
                                     !string.IsNullOrWhiteSpace(param.Value.GetString()))
                            {
                                parameters[param.Key] = param.Value.GetString().Trim();
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore JSON parse errors
                }
            }

            return (parameters, nextToken, limit);
        }

        private static object ConvertAttributeValue(AttributeValue value)
        {
            if (value.S != null) return value.S;
            if (value.N != null) return value.N;
            if (value.BOOL.HasValue) return value.BOOL.Value;
            if (value.IsMSet) return value.M.ToDictionary(kv => kv.Key, kv => ConvertAttributeValue(kv.Value));
            if (value.IsLSet) return value.L.Select(ConvertAttributeValue).ToList();
            if (value.SS != null) return value.SS;
            if (value.NS != null) return value.NS;
            if (value.BS != null) return value.BS;
            if (value.IsNull) return null;
            return value.ToString();
        }

        private static APIGatewayHttpApiV2ProxyResponse CreateResponse(string message, HttpStatusCode statusCode)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = (int)statusCode,
                Body = JsonSerializer.Serialize(new { error = message }),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
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
}