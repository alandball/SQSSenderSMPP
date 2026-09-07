using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.SQS;

namespace Sender.Aws;

public sealed class AwsOptions
{
    public string Region { get; set; } = "";
    public string QueueUrl { get; set; } = "";
    public string LedgerTable { get; set; } = "";
    public string? ServiceUrl { get; set; }
    public bool LocalEmulator { get; set; }

    public void Validate(bool development)
    {
        if (string.IsNullOrWhiteSpace(Region) || string.IsNullOrWhiteSpace(LedgerTable) ||
            !Uri.TryCreate(QueueUrl, UriKind.Absolute, out var queue))
            throw new InvalidOperationException("Aws requires Region, QueueUrl and LedgerTable.");
        if (LocalEmulator)
        {
            if (!development || !Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var service) ||
                !service.IsLoopback || !queue.IsLoopback || service.Authority != queue.Authority)
                throw new InvalidOperationException("Local emulation requires Development and matching loopback endpoints.");
        }
        else if (ServiceUrl is not null || queue.Scheme != Uri.UriSchemeHttps || queue.IsLoopback)
            throw new InvalidOperationException("Work mode requires HTTPS SQS and the AWS credential chain; custom endpoints are local-only.");
    }

    public AmazonSQSClient CreateSqs() => LocalEmulator
        ? new(new BasicAWSCredentials("local", "local"), new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = Region, MaxErrorRetry = 2 })
        : new(new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(Region), MaxErrorRetry = 2 });

    public AmazonDynamoDBClient CreateDynamo() => LocalEmulator
        ? new(new BasicAWSCredentials("local", "local"), new AmazonDynamoDBConfig { ServiceURL = ServiceUrl, AuthenticationRegion = Region, MaxErrorRetry = 2 })
        : new(new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(Region), MaxErrorRetry = 2 });
}
