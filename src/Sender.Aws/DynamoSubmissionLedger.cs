using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Sender.Core;

namespace Sender.Aws;

public sealed class DynamoSubmissionLedger(IAmazonDynamoDB client, AwsOptions options) : ISubmissionLedger
{
    public async Task<LedgerEntry?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = options.LedgerTable,
            Key = Key(key),
            ConsistentRead = true
        }, cancellationToken);
        return response.Item is not { Count: > 0 } ? null : JsonSerializer.Deserialize(response.Item["data"].S, ContractJson.Default.LedgerEntry)
            ?? throw new InvalidDataException("Invalid ledger record.");
    }

    public async Task<bool> WriteAsync(LedgerEntry entry, long? expectedRevision, CancellationToken cancellationToken)
    {
        var request = new PutItemRequest
        {
            TableName = options.LedgerTable,
            Item = new()
            {
                ["pk"] = new() { S = entry.Key },
                ["revision"] = new() { N = entry.Revision.ToString(CultureInfo.InvariantCulture) },
                ["state"] = new() { S = entry.State.ToString() },
                ["expiresAt"] = new() { N = entry.ExpiresAt.ToString(CultureInfo.InvariantCulture) },
                ["data"] = new() { S = JsonSerializer.Serialize(entry, ContractJson.Default.LedgerEntry) }
            },
            ConditionExpression = expectedRevision is null ? "attribute_not_exists(pk)" : "revision = :expected",
            ExpressionAttributeValues = expectedRevision is null ? null : new()
            { [":expected"] = new() { N = expectedRevision.Value.ToString(CultureInfo.InvariantCulture) } }
        };
        try { await client.PutItemAsync(request, cancellationToken); return true; }
        catch (ConditionalCheckFailedException) { return false; }
        // Timeout is not a miss. Caller retains the envelope and rereads on redelivery.
    }

    public async Task ProbeAsync(CancellationToken cancellationToken) => await client.GetItemAsync(new GetItemRequest
    {
        TableName = options.LedgerTable,
        Key = Key("_health"),
        ConsistentRead = true,
        ProjectionExpression = "pk"
    }, cancellationToken);

    private static Dictionary<string, AttributeValue> Key(string key) => new() { ["pk"] = new() { S = key } };
}
