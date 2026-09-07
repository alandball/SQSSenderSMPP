namespace Sender.Core;

public sealed record DeliveryReceipt(string ProviderProfile, string MessageId, string State, bool IsFinal, DateTimeOffset ReceivedAt);

public interface IDeliveryReceiptStore
{
    Task StoreAsync(DeliveryReceipt receipt, CancellationToken cancellationToken);
}
