using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using SottoTeamsBot.Bot;
using System.Collections.Concurrent;

namespace SottoTeamsBot.Aws;

public sealed class DynamoResolver
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tenantsTable;
    private readonly string _agentsTable;
    private readonly ConcurrentDictionary<string, string> _tenantCache = new();
    private readonly ConcurrentDictionary<string, string?> _agentCache = new();

    public DynamoResolver(IAmazonDynamoDB dynamo, IOptions<BotOptions> options)
    {
        _dynamo = dynamo;
        _tenantsTable = options.Value.DynamoTenantsTable;
        _agentsTable = options.Value.DynamoAgentsTable;
    }

    public async Task<string?> ResolveTenantIdAsync(string msTenantId)
    {
        if (_tenantCache.TryGetValue(msTenantId, out var cached))
            return cached;

        try
        {
            var response = await _dynamo.QueryAsync(new QueryRequest
            {
                TableName = _tenantsTable,
                IndexName = "ms-tenant-index",
                KeyConditionExpression = "ms_tenant_id = :v",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":v"] = new AttributeValue { S = msTenantId }
                },
                Limit = 1
            });

            var item = response.Items.FirstOrDefault();
            if (item == null || !item.TryGetValue("tenant_id", out var attr))
                return null;

            var tenantId = attr.S;
            if (tenantId != null)
                _tenantCache.TryAdd(msTenantId, tenantId);

            return tenantId;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> ResolveAgentIdAsync(string msTenantId, string msUserId)
    {
        var cacheKey = $"{msTenantId}#{msUserId}";
        if (_agentCache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var response = await _dynamo.QueryAsync(new QueryRequest
            {
                TableName = _agentsTable,
                IndexName = "ms-user-index",
                KeyConditionExpression = "ms_user_id = :v",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":v"] = new AttributeValue { S = msUserId }
                },
                Limit = 1
            });

            var item = response.Items.FirstOrDefault();
            var agentId = item != null && item.TryGetValue("agent_id", out var attr)
                ? attr.S
                : null;

            _agentCache.TryAdd(cacheKey, agentId);
            return agentId;
        }
        catch
        {
            return null;
        }
    }
}
