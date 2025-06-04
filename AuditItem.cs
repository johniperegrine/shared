using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2.DataModel;

namespace AWSTest
{
    [DynamoDBTable("user_audit_table")]
    public class AuditItem
    {
        // GSI Keys
        [DynamoDBGlobalSecondaryIndexHashKey("userId-index")]
        [DynamoDBProperty("userId")]
        [JsonPropertyName("userId")]
        public string UserId { get; set; } = string.Empty;

        [DynamoDBGlobalSecondaryIndexHashKey("applicationId-index")]
        [DynamoDBProperty("applicationId")]
        [JsonPropertyName("applicationId")]
        public string ApplicationId { get; set; } = string.Empty;

        [DynamoDBGlobalSecondaryIndexHashKey("resourceId-index")]
        [DynamoDBProperty("resourceId")]
        [JsonPropertyName("resourceId")]
        public string ResourceId { get; set; } = string.Empty;

        // Common attributes with both DynamoDB and JSON serialization support
        [DynamoDBProperty("auditId")]
        [JsonPropertyName("auditId")]
        public string AuditId { get; set; } = string.Empty;

        [DynamoDBProperty("eventTimestamp")]
        [JsonPropertyName("eventTimestamp")]
        public string EventTimestamp { get; set; } = string.Empty;

        [DynamoDBProperty("systemId")]
        [JsonPropertyName("systemId")]
        public string SystemId { get; set; } = string.Empty;

        [DynamoDBProperty("systemName")]
        [JsonPropertyName("systemName")]
        public string SystemName { get; set; } = string.Empty;

        [DynamoDBProperty("environment")]
        [JsonPropertyName("environment")]
        public string Environment { get; set; } = string.Empty;

        [DynamoDBProperty("email")]
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [DynamoDBProperty("role")]
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [DynamoDBProperty("actionType")]
        [JsonPropertyName("actionType")]
        public string ActionType { get; set; } = string.Empty;

        [DynamoDBProperty("resourceType")]
        [JsonPropertyName("resourceType")]
        public string ResourceType { get; set; } = string.Empty;

        [DynamoDBProperty("actionDescription")]
        [JsonPropertyName("actionDescription")]
        public string ActionDescription { get; set; } = string.Empty;

        [DynamoDBProperty("dataBefore")]
        [JsonPropertyName("dataBefore")]
        public string DataBefore { get; set; } = string.Empty;

        [DynamoDBProperty("dataAfter")]
        [JsonPropertyName("dataAfter")]
        public string DataAfter { get; set; } = string.Empty;

        [DynamoDBProperty("retentionPeriodInYears")]
        [JsonPropertyName("retentionPeriodInYears")]
        public int RetentionPeriodInYears { get; set; }

        [DynamoDBProperty("deletionDate")]
        [JsonPropertyName("deletionDate")]
        public string DeletionDate { get; set; } = string.Empty;
    }
}
