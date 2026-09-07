using Amazon.SQS;
using Amazon.SQS.Model;
using Sender.Core;

namespace Sender.Aws;

public sealed class SqsEnvelopeQueue(IAmazonSQS client, AwsOptions aws, SenderOptions options) : IEnvelopeQueue
{
    public async Task<QueueReceipt?> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Reserve one envelope slot before calling. One receive never expands to 500 children.
        var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = aws.QueueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 20,
            VisibilityTimeout = options.VisibilitySeconds
        }, cancellationToken);
        var message = response.Messages?.SingleOrDefault();
        return message is null ? null : new(message.Body, message.ReceiptHandle);
    }

    public async Task ExtendAsync(string handle, CancellationToken cancellationToken) =>
        await client.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = aws.QueueUrl,
            ReceiptHandle = handle,
            VisibilityTimeout = options.VisibilitySeconds
        }, cancellationToken);

    public async Task DeleteAsync(string handle, CancellationToken cancellationToken) =>
        await client.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = aws.QueueUrl, ReceiptHandle = handle }, cancellationToken);
}
