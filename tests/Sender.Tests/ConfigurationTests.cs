using Sender.Aws;
using Sender.Core;
using Sender.Smpp;

namespace Sender.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Production_rejects_emulator_configuration()
    {
        var aws = new AwsOptions
        {
            Region = "us-east-1",
            QueueUrl = "http://127.0.0.1:5000/account/queue",
            ServiceUrl = "http://127.0.0.1:5000",
            LedgerTable = "local",
            LocalEmulator = true
        };
        Assert.Throws<InvalidOperationException>(() => aws.Validate(false));
        aws.Validate(true);
    }

    [Fact]
    public void Emulator_never_targets_a_remote_host()
    {
        var aws = new AwsOptions
        {
            Region = "us-east-1",
            QueueUrl = "http://example.com/account/queue",
            ServiceUrl = "http://example.com",
            LedgerTable = "local",
            LocalEmulator = true
        };
        Assert.Throws<InvalidOperationException>(() => aws.Validate(true));
    }

    [Fact]
    public void Lease_must_outlive_protocol_timeout_and_result_persistence()
    {
        var smpp = new SmppOptions { Host = "127.0.0.1", SystemId = "local", Password = "local", UseTls = false, ResponseTimeoutSeconds = 60 };
        Assert.Throws<InvalidOperationException>(() => smpp.Validate(true, new SenderOptions { LeaseSeconds = 60 }));
    }

    [Fact]
    public void Retry_classification_cannot_include_success_or_local_error_codes()
    {
        var smpp = new SmppOptions { Host = "127.0.0.1", SystemId = "local", Password = "local", UseTls = false, RetryableStatuses = [0] };
        Assert.Throws<InvalidOperationException>(() => smpp.Validate(true, new()));
    }
}
