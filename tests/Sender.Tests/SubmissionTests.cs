using Sender.Core;

namespace Sender.Tests;

public sealed class SubmissionTests
{
    [Fact]
    public async Task Exhausted_retries_block_deletion_and_further_submits_for_dlq_redrive()
    {
        using var f = new ProcessorFixture();
        f.Options.MaxAttempts = 1;
        f.Session.OnSubmit = (_, _) => Task.FromResult(new SubmitResult(SubmitOutcome.Retryable, "throttle"));
        var envelope = Samples.Envelope();
        Assert.False(await f.Process(envelope));
        f.Clock.Advance();
        Assert.False(await f.Process(envelope));
        Assert.Equal(SubmissionState.RetryExhausted, (await f.Read(envelope))!.State);
        Assert.Equal(1, f.Session.Count);
    }

    [Fact]
    public async Task Accepted_redelivery_never_submits_again()
    {
        using var f = new ProcessorFixture();
        var envelope = Samples.Envelope();
        Assert.True(await f.Process(envelope));
        Assert.True(await f.Process(envelope with { EnvelopeId = Guid.NewGuid() }));
        Assert.Equal(1, f.Session.Count);
    }

    [Fact]
    public async Task Competing_consumers_have_one_owner()
    {
        using var f = new ProcessorFixture();
        var envelope = Samples.Envelope();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Session.OnSubmit = async (_, _) => { started.SetResult(); await release.Task; return new(SubmitOutcome.Accepted, "ok", "one"); };
        var first = f.Process(envelope);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var competing = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => f.Process(envelope)));
        Assert.All(competing, result => Assert.False(result));
        release.SetResult();
        Assert.True(await first);
        Assert.Equal(1, f.Session.Count);
    }

    [Fact]
    public async Task Payload_conflict_is_not_a_dedupe_hit()
    {
        using var f = new ProcessorFixture();
        var envelope = Samples.Envelope();
        await f.Process(envelope);
        var altered = envelope with { Messages = [envelope.Messages[0] with { Text = "Different" }] };
        await Assert.ThrowsAsync<ContractException>(() => f.Process(altered));
        Assert.Equal(1, f.Session.Count);
    }

    [Theory]
    [InlineData(SubmitOutcome.Unknown, SubmissionState.Unknown)]
    [InlineData(SubmitOutcome.PermanentRejected, SubmissionState.PermanentRejected)]
    public async Task Terminal_failure_is_not_retried(SubmitOutcome outcome, SubmissionState expected)
    {
        using var f = new ProcessorFixture();
        f.Session.OnSubmit = (_, _) => Task.FromResult(new SubmitResult(outcome, "injected"));
        var envelope = Samples.Envelope();
        Assert.True(await f.Process(envelope));
        f.Clock.Advance();
        Assert.True(await f.Process(envelope));
        Assert.Equal(1, f.Session.Count);
        Assert.Equal(expected, (await f.Read(envelope))!.State);
    }

    [Fact]
    public async Task Lost_acceptance_write_recovers_as_unknown_not_a_resend()
    {
        using var f = new ProcessorFixture();
        var envelope = Samples.Envelope();
        f.Ledger.FailAcceptedWrites = true;
        await Assert.ThrowsAsync<IOException>(() => f.Process(envelope));
        Assert.Equal(SubmissionState.SendStarted, (await f.Read(envelope))!.State);
        f.Ledger.FailAcceptedWrites = false;
        f.Clock.Advance();
        Assert.True(await f.Process(envelope));
        Assert.Equal(SubmissionState.Unknown, (await f.Read(envelope))!.State);
        Assert.Equal(1, f.Session.Count);
    }

    [Fact]
    public async Task Multipart_retry_preserves_accepted_segments()
    {
        using var f = new ProcessorFixture();
        f.Session.Segments = 3;
        var segments = new List<int>();
        f.Session.OnSubmit = (i, _) =>
        {
            segments.Add(i);
            return Task.FromResult(i == 1 && segments.Count == 2
                ? new SubmitResult(SubmitOutcome.Retryable, "throttle") : new SubmitResult(SubmitOutcome.Accepted, "ok", $"id-{i}"));
        };
        var envelope = Samples.Envelope();
        Assert.False(await f.Process(envelope));
        Assert.Equal(SegmentState.Accepted, (await f.Read(envelope))!.Segments[0].State);
        f.Clock.Advance();
        Assert.True(await f.Process(envelope));
        Assert.Equal([0, 1, 1, 2], segments);
    }

    [Fact]
    public async Task Cancellation_after_send_started_is_durably_unknown()
    {
        using var f = new ProcessorFixture();
        using var cancellation = new CancellationTokenSource();
        f.Session.OnSubmit = (_, _) => { cancellation.Cancel(); throw new OperationCanceledException(); };
        var envelope = Samples.Envelope();
        Assert.True(await f.Process(envelope, cancellation.Token));
        Assert.Equal(SubmissionState.Unknown, (await f.Read(envelope))!.State);
    }

    [Fact]
    public async Task Ledger_failure_never_falls_through_to_send()
    {
        using var f = new ProcessorFixture();
        f.Ledger.FailReads = true;
        await Assert.ThrowsAsync<IOException>(() => f.Process(Samples.Envelope()));
        Assert.Equal(0, f.Session.Count);
    }

    [Fact]
    public async Task Expired_sms_is_rejected_without_submission()
    {
        using var f = new ProcessorFixture();
        var envelope = Samples.Envelope();
        envelope = envelope with { Messages = [envelope.Messages[0] with { ValidUntil = f.Clock.Now.AddSeconds(-1) }] };
        Assert.True(await f.Process(envelope));
        Assert.Equal(0, f.Session.Count);
        Assert.Equal("expired", (await f.Read(envelope))!.Reason);
    }

    [Fact]
    public async Task Stale_owner_cannot_overwrite_quarantine()
    {
        using var f = new ProcessorFixture();
        var envelope = Samples.Envelope();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Session.OnSubmit = async (_, _) => { started.SetResult(); await release.Task; return new(SubmitOutcome.Accepted, "ok", "late"); };
        var first = f.Process(envelope);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Clock.Advance();
        Assert.True(await f.Process(envelope));
        release.SetResult();
        Assert.False(await first);
        Assert.Equal(SubmissionState.Unknown, (await f.Read(envelope))!.State);
        Assert.Equal(1, f.Session.Count);
    }
}
