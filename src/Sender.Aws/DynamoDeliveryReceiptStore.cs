using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Sender.Core;

namespace Sender.Aws;

// DLRs can arrive before submit_sm_resp. Store independently, keyed by provider/message ID.
// Join from LedgerEntry.Segments[].MessageId; submission completion never waits for a DLR.
public sealed class DynamoDeliveryReceiptStore(IAmazonDynamoDB client, AwsOptions aws, SenderOptions sender) : IDeliveryReceiptStore
{
    public static string Key(string provider, string messageId) => $"dlr/{provider}/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))}";

    public async Task StoreAsync(DeliveryReceipt receipt, CancellationToken cancellationToken)
    {
        try
        {
            await client.PutItemAsync(new PutItemRequest
            {
                TableName = aws.LedgerTable,
                Item = new()
                {
                    ["pk"] = new() { S = Key(receipt.ProviderProfile, receipt.MessageId) },
                    ["deliveryState"] = new() { S = receipt.State },
                    ["deliveryFinal"] = new() { BOOL = receipt.IsFinal },
                    ["receivedAt"] = new() { S = receipt.ReceivedAt.ToString("O", CultureInfo.InvariantCulture) },
                    ["expiresAt"] = new() { N = receipt.ReceivedAt.AddDays(sender.RetentionDays).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) }
                },
                // First terminal receipt wins; delayed intermediates cannot regress delivery state.
                ConditionExpression = "attribute_not_exists(pk) OR deliveryFinal = :no",
                ExpressionAttributeValues = new() { [":no"] = new() { BOOL = false } }
            }, cancellationToken);
        }
        catch (ConditionalCheckFailedException) { /* Durable terminal receipt already exists. */ }
    }
}
