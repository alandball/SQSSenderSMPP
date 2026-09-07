using Microsoft.Extensions.Logging.Abstractions;
using Sender.Core;
using Sender.Smpp;

namespace Sender.Tests;

public sealed class EncodingTests
{
    [Theory]
    [InlineData("a", 160, 1)]
    [InlineData("a", 161, 2)]
    [InlineData("^", 80, 1)]
    [InlineData("^", 81, 2)]
    [InlineData("中", 70, 1)]
    [InlineData("中", 71, 2)]
    public async Task Gsm_extensions_and_ucs2_obey_segment_boundaries(string character, int count, int expected)
    {
        await using var session = Create();
        var sms = Samples.Envelope().Messages[0] with { Text = string.Concat(Enumerable.Repeat(character, count)) };
        Assert.Equal(expected, session.Prepare(sms).SegmentCount);
    }

    [Fact]
    public async Task Non_bmp_text_is_explicitly_rejected()
    {
        await using var session = Create();
        Assert.Throws<MessagePolicyException>(() => session.Prepare(Samples.Envelope().Messages[0] with { Text = "😀" }));
    }

    [Fact]
    public async Task Timeout_status_is_never_retryable()
    {
        await using var session = Create();
        Assert.Equal(SubmitOutcome.Unknown, session.Classify((uint)Inetlab.SMPP.Common.CommandStatus.SMPPCLIENT_RCVTIMEOUT, null).Outcome);
        Assert.Equal(SubmitOutcome.Retryable, session.Classify(0x58, null).Outcome);
        Assert.Equal(SubmitOutcome.Unknown, session.Classify(0, "").Outcome);
    }

    internal static InetlabSession Create(int port = 2775, IDeliveryReceiptStore? store = null) => new(new SmppOptions
    {
        Host = "127.0.0.1",
        Port = port,
        SystemId = "local",
        Password = "local",
        UseTls = false,
        ResponseTimeoutSeconds = 2,
        EnquireLinkSeconds = 1
    }, new SenderOptions(), store ?? new ReceiptStore(), NullLogger<InetlabSession>.Instance);

    internal sealed class ReceiptStore : IDeliveryReceiptStore
    {
        public Task StoreAsync(DeliveryReceipt receipt, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
