namespace AWSTest
{
    public interface IDynamoDBService
    {
        Task<IEnumerable<AuditItem>> GetAuditItemsAsync(Dictionary<string, string> qsFilters);
    }
}