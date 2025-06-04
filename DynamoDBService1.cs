using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;

namespace AWSTest
{
    public interface IDynamoDBService
    {
        Task<IEnumerable<AuditItem>> GetAuditItemsAsync(Dictionary<string, string> queryParameters);
    }

    public class DynamoDBService(IAmazonDynamoDB dynamoDBClient) : IDynamoDBService
    {
        private readonly IAmazonDynamoDB _client = dynamoDBClient;
        private readonly IDynamoDBContext _context = new DynamoDBContextBuilder().WithDynamoDBClient(() => dynamoDBClient).Build();
        private readonly string _tableName = Environment.GetEnvironmentVariable("AUDIT_TABLE") ?? "user_audit_table";
        private readonly string[] _indexFieldNames = { "userId", "applicationId", "resourceId" };

        public async Task<IEnumerable<AuditItem>> GetAuditItemsAsync(Dictionary<string, string> filters)
        {
            if (filters?.Any() != true)
                throw new ArgumentException("At least one query parameter is required");

            string indexField = null;
            string indexName = null;
            string hashKey = null;
            var filterExpressions = new List<string>();
            var attributeNames = new Dictionary<string, string>();
            var attributeValues = new Dictionary<string, AttributeValue>();

            // Process filters in a single pass
            foreach (var (key, value) in filters)
            {
                if (string.IsNullOrEmpty(value)) continue;

                if (indexField == null && _indexFieldNames.Contains(key))
                {
                    indexField = key;
                    indexName = $"{key}-index";
                    hashKey = value;
                    attributeNames["#" + key] = key;
                    attributeValues[":hashKey"] = new AttributeValue { S = value };
                    continue;
                }

                if (key == "startDate")
                {
                    attributeNames["#ts"] = "eventTimestamp";
                    attributeValues[":" + key] = new AttributeValue { S = value };
                    filterExpressions.Add($"#ts >= :{key}");
                }
                else if (key == "endDate")
                {
                    attributeNames["#ts"] = "eventTimestamp";
                    attributeValues[":" + key] = new AttributeValue { S = value };
                    filterExpressions.Add($"#ts <= :{key}");
                }
                else
                {
                    attributeNames["#" + key] = key;
                    attributeValues[":" + key] = new AttributeValue { S = value };
                    filterExpressions.Add($"#{key} = :{key}");
                }
            }

            if (indexField == null)
                throw new ArgumentException($"Must include one of: {string.Join(", ", _indexFieldNames)}");

            var results = new List<AuditItem>();
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = indexName,
                KeyConditionExpression = $"#{indexField} = :hashKey",
                ExpressionAttributeNames = attributeNames,
                ExpressionAttributeValues = attributeValues,
                FilterExpression = filterExpressions.Any() ? string.Join(" AND ", filterExpressions) : null
            };

            QueryResponse response;
            do
            {
                response = await _client.QueryAsync(request);

                // Use DynamoDBContext to map items to AuditItem
                foreach (var item in response.Items)
                {
                    var document = Document.FromAttributeMap(item);
                    results.Add(_context.FromDocument<AuditItem>(document));
                }

                request.ExclusiveStartKey = response.LastEvaluatedKey;
            } while (response.LastEvaluatedKey?.Count > 0);

            return results;
        }
    }
}