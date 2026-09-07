using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Sender.Aws;
using Sender.Core;
using Sender.Harness;

namespace Sender.Tests;

[Trait("Category", "Integration")]
public sealed class IntegrationTests
{
    [Fact]
    public async Task Poison_envelope_reaches_configured_dlq_without_any_smpp_submission()
    {
        await using var env = await AwsFixture.CreateAsync();
        var dlq = (await env.Sqs.CreateQueueAsync(env.Aws.LedgerTable + "-dlq", env.Token)).QueueUrl;
        try
        {
            var arn = (await env.Sqs.GetQueueAttributesAsync(dlq, ["QueueArn"], env.Token)).Attributes["QueueArn"];
            await env.Sqs.SetQueueAttributesAsync(env.Aws.QueueUrl, new()
            {
                ["RedrivePolicy"] = JsonSerializer.Serialize(new { deadLetterTargetArn = arn, maxReceiveCount = 2 })
            }, env.Token);
            await env.Sqs.SendMessageAsync(env.Aws.QueueUrl, "{}", env.Token);
            var options = new SenderOptions { VisibilitySeconds = 3, HeartbeatSeconds = 1, ShutdownSeconds = 5 };
            var session = new FakeSession();
            using var gate = new SubmitGate(options);
            var processor = new SubmissionProcessor(env.Ledger, session, gate, options, TimeProvider.System, NullLogger<SubmissionProcessor>.Instance);
            using var worker = new SenderService(new SqsEnvelopeQueue(env.Sqs, env.Aws, options), env.Ledger, session, processor, gate, options, NullLogger<SenderService>.Instance);
            await worker.StartAsync(env.Token);
            while (true)
            {
                var response = await env.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = dlq, WaitTimeSeconds = 1 }, env.Token);
                if (response.Messages is { Count: > 0 }) { Assert.Equal("{}", response.Messages[0].Body); break; }
            }
            await worker.StopAsync(env.Token);
            Assert.Equal(0, session.Count);
        }
        finally { await env.Sqs.DeleteQueueAsync(dlq, env.Token); }
    }

    [Fact]
    public async Task Dynamo_conditional_claim_and_stale_revision_are_atomic()
    {
        await using var env = await AwsFixture.CreateAsync();
        var entry = new LedgerEntry
        {
            Key = "test/atomic",
            PayloadHash = "hash",
            ProfileHash = "profile",
            EnvelopeId = Guid.NewGuid(),
            Owner = "a",
            Segments = [new(SegmentState.Pending)],
            State = SubmissionState.Claimed
        };
        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => env.Ledger.WriteAsync(entry, null, env.Token)));
        Assert.Single(claims, x => x);
        Assert.True(await env.Ledger.WriteAsync(entry with { Revision = 1, State = SubmissionState.SendStarted }, 0, env.Token));
        Assert.False(await env.Ledger.WriteAsync(entry with { Revision = 1, State = SubmissionState.Accepted }, 0, env.Token));
        Assert.Equal(SubmissionState.SendStarted, (await env.Ledger.ReadAsync(entry.Key, env.Token))!.State);
    }

    [Fact]
    public async Task Worker_completes_50_children_and_deduplicates_replayed_envelope()
    {
        await using var env = await AwsFixture.CreateAsync();
        var port = FreePort();
        await using var simulator = new Simulator(port);
        await simulator.StartAsync();
        await using var session = EncodingTests.Create(port);
        var options = new SenderOptions { PerBindTps = 100, Window = 4, MaxActiveEnvelopes = 8, ShutdownSeconds = 5 };
        using var gate = new SubmitGate(options);
        var processor = new SubmissionProcessor(env.Ledger, session, gate, options, TimeProvider.System, NullLogger<SubmissionProcessor>.Instance);
        using var worker = new SenderService(new SqsEnvelopeQueue(env.Sqs, env.Aws, options), env.Ledger, session, processor, gate, options, NullLogger<SenderService>.Instance);
        var envelope = Samples.Envelope(50);
        await env.SendAsync(envelope);
        await worker.StartAsync(env.Token);
        await env.WaitEmptyAsync();
        Assert.Equal(50, simulator.Accepted);
        foreach (var sms in envelope.Messages)
            Assert.Equal(SubmissionState.Accepted, (await env.Ledger.ReadAsync(EnvelopeContract.Key(envelope, sms), env.Token))!.State);
        await env.SendAsync(envelope);
        await env.WaitEmptyAsync();
        Assert.Equal(50, simulator.Accepted);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StopAsync(stop.Token);
        Assert.False(worker.IsReady);
    }

    [Theory]
    [InlineData("timeout", SubmissionState.Unknown)]
    [InlineData("disconnect", SubmissionState.Unknown)]
    [InlineData("permanent", SubmissionState.PermanentRejected)]
    public async Task Real_protocol_faults_are_terminal_without_blind_retry(string scenario, SubmissionState expected)
    {
        await using var env = await AwsFixture.CreateAsync();
        var port = FreePort();
        await using var simulator = new Simulator(port) { Scenario = scenario };
        await simulator.StartAsync();
        await using var session = EncodingTests.Create(port);
        await session.ConnectAsync(env.Token);
        Assert.True(session.IsBound);
        var options = new SenderOptions { PerBindTps = 100 };
        using var gate = new SubmitGate(options);
        var processor = new SubmissionProcessor(env.Ledger, session, gate, options, TimeProvider.System, NullLogger<SubmissionProcessor>.Instance);
        var envelope = Samples.Envelope();
        Assert.True(await processor.ProcessAsync(envelope, envelope.Messages[0], env.Token));
        Assert.Equal(expected, (await env.Ledger.ReadAsync(EnvelopeContract.Key(envelope, envelope.Messages[0]), env.Token))!.State);
        Assert.True(await processor.ProcessAsync(envelope, envelope.Messages[0], env.Token));
        Assert.Equal(1, simulator.Requests);
    }

    [Fact]
    public async Task Dlr_before_submit_response_is_durable_and_final_state_cannot_regress()
    {
        await using var env = await AwsFixture.CreateAsync();
        var port = FreePort();
        await using var simulator = new Simulator(port) { Scenario = "dlr" };
        await simulator.StartAsync();
        var store = new DynamoDeliveryReceiptStore(env.Dynamo, env.Aws, new());
        await using var session = EncodingTests.Create(port, store);
        await session.ConnectAsync(env.Token);
        var result = await session.SubmitAsync(session.Prepare(Samples.Envelope().Messages[0]), 0, env.Token);
        Assert.Equal(SubmitOutcome.Accepted, result.Outcome);
        Assert.Equal(1, simulator.ReceiptAcks);
        await store.StoreAsync(new("default", result.MessageId!, "Enroute", false, DateTimeOffset.UtcNow), env.Token);
        var receipt = await env.Dynamo.GetItemAsync(env.Aws.LedgerTable, new() { ["pk"] = new() { S = DynamoDeliveryReceiptStore.Key("default", result.MessageId!) } }, env.Token);
        Assert.Equal("Delivered", receipt.Item["deliveryState"].S);
        Assert.True(receipt.Item["deliveryFinal"].BOOL);
    }

    [Fact]
    public async Task Out_of_order_responses_are_correlated_to_their_submission()
    {
        await using var env = await AwsFixture.CreateAsync();
        var port = FreePort();
        await using var simulator = new Simulator(port) { Scenario = "out-of-order" };
        await simulator.StartAsync();
        await using var session = EncodingTests.Create(port);
        await session.ConnectAsync(env.Token);
        var prepared = Samples.Envelope(4).Messages.Select(session.Prepare).ToArray();
        var first = session.SubmitAsync(prepared[0], 0, env.Token);
        // Submission order is controlled; responses deliberately arrive in reverse pairs.
        await Task.Delay(30, env.Token);
        var second = session.SubmitAsync(prepared[1], 0, env.Token);
        var responses = await Task.WhenAll(first, second);
        Assert.All(responses, r => Assert.Equal(SubmitOutcome.Accepted, r.Outcome));
        Assert.Equal("local-1", responses[0].MessageId);
        Assert.Equal("local-2", responses[1].MessageId);
    }

    [Fact]
    public async Task Throttle_is_explicitly_retryable_and_second_attempt_succeeds()
    {
        await using var env = await AwsFixture.CreateAsync();
        var port = FreePort();
        await using var simulator = new Simulator(port) { Scenario = "throttle-once" };
        await simulator.StartAsync();
        await using var session = EncodingTests.Create(port);
        await session.ConnectAsync(env.Token);
        var clock = new TestClock();
        var options = new SenderOptions { PerBindTps = 100 };
        using var gate = new SubmitGate(options);
        var processor = new SubmissionProcessor(env.Ledger, session, gate, options, clock, NullLogger<SubmissionProcessor>.Instance);
        var envelope = Samples.Envelope();
        Assert.False(await processor.ProcessAsync(envelope, envelope.Messages[0], env.Token));
        clock.Advance();
        Assert.True(await processor.ProcessAsync(envelope, envelope.Messages[0], env.Token));
        Assert.Equal(2, simulator.Requests);
        Assert.Equal(1, simulator.Accepted);
    }

    [Fact]
    public async Task Sqs_visibility_extension_and_delete_use_current_receipt()
    {
        await using var env = await AwsFixture.CreateAsync();
        await env.SendAsync(Samples.Envelope());
        var options = new SenderOptions { VisibilitySeconds = 3 };
        var queue = new SqsEnvelopeQueue(env.Sqs, env.Aws, options);
        var receipt = await queue.ReceiveAsync(env.Token);
        Assert.NotNull(receipt);
        await Task.Delay(2000, env.Token);
        await queue.ExtendAsync(receipt.Handle, env.Token);
        await Task.Delay(1500, env.Token);
        var hidden = await env.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = env.Aws.QueueUrl, WaitTimeSeconds = 0 }, env.Token);
        Assert.True(hidden.Messages is null or { Count: 0 });
        await queue.DeleteAsync(receipt.Handle, env.Token);
        await env.WaitEmptyAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed class AwsFixture : IAsyncDisposable
{
    public AwsOptions Aws { get; } = new()
    {
        Region = "us-east-1",
        ServiceUrl = "http://127.0.0.1:5000",
        LocalEmulator = true,
        LedgerTable = "test-" + Guid.NewGuid().ToString("N")
    };
    public AmazonSQSClient Sqs { get; }
    public AmazonDynamoDBClient Dynamo { get; }
    public DynamoSubmissionLedger Ledger { get; }
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(90));
    public CancellationToken Token => _deadline.Token;

    private AwsFixture()
    {
        Sqs = Aws.CreateSqs();
        Dynamo = Aws.CreateDynamo();
        Ledger = new(Dynamo, Aws);
    }

    public static async Task<AwsFixture> CreateAsync()
    {
        var fixture = new AwsFixture();
        try
        {
            var response = await fixture.Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = fixture.Aws.LedgerTable }, fixture.Token);
            fixture.Aws.QueueUrl = response.QueueUrl;
            await fixture.Dynamo.CreateTableAsync(new CreateTableRequest
            {
                TableName = fixture.Aws.LedgerTable,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                AttributeDefinitions = [new("pk", ScalarAttributeType.S)],
                KeySchema = [new("pk", KeyType.HASH)]
            }, fixture.Token);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw new InvalidOperationException("Local AWS emulator unavailable. Run pwsh ./build.ps1 integration-up before this suite.");
        }
    }

    public Task SendAsync(SmsEnvelope envelope) => Sqs.SendMessageAsync(new SendMessageRequest
    { QueueUrl = Aws.QueueUrl, MessageBody = JsonSerializer.Serialize(envelope, ContractJson.Default.SmsEnvelope) }, Token);

    public async Task WaitEmptyAsync()
    {
        while (true)
        {
            var state = await Sqs.GetQueueAttributesAsync(Aws.QueueUrl, ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"], Token);
            if (state.Attributes.Values.All(v => v == "0")) return;
            await Task.Delay(100, Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (!string.IsNullOrEmpty(Aws.QueueUrl)) await Sqs.DeleteQueueAsync(Aws.QueueUrl, cleanup.Token);
            await Dynamo.DeleteTableAsync(Aws.LedgerTable, cleanup.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        Sqs.Dispose();
        Dynamo.Dispose();
        _deadline.Dispose();
    }
}
