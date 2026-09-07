using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Sender.Core;

namespace Sender.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task Shutdown_stops_intake_but_finishes_admitted_submission_before_delete()
    {
        using var f = new ProcessorFixture();
        var queue = new TestQueue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Session.OnSubmit = async (_, ct) => { started.SetResult(); await release.Task.WaitAsync(ct); return new(SubmitOutcome.Accepted, "ok", "one"); };
        using var worker = new SenderService(queue, f.Ledger, f.Session, f.Processor, f.Gate, f.Options, NullLogger<SenderService>.Instance);
        queue.Add(Samples.Envelope());
        await worker.StartAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = worker.StopAsync(default);
        await Task.Delay(50);
        Assert.False(worker.IsReady);
        Assert.False(stopping.IsCompleted);
        Assert.Equal(0, queue.Deleted);
        release.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, queue.Deleted);
        Assert.False(f.Session.IsBound);
    }

    [Fact]
    public async Task Unresolved_child_blocks_delete_while_other_children_progress()
    {
        using var f = new ProcessorFixture();
        var queue = new TestQueue();
        f.Session.OnSubmit = (_, _) => Task.FromResult(f.Session.Count == 1
            ? new SubmitResult(SubmitOutcome.Retryable, "throttle") : new SubmitResult(SubmitOutcome.Accepted, "ok", "two"));
        var envelope = Samples.Envelope(2);
        using var worker = new SenderService(queue, f.Ledger, f.Session, f.Processor, f.Gate, f.Options, NullLogger<SenderService>.Instance);
        queue.Add(envelope);
        await worker.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await f.Ledger.ReadAsync(EnvelopeContract.Key(envelope, envelope.Messages[1]), deadline.Token))?.IsTerminal != true)
            await Task.Delay(20, deadline.Token);
        await worker.StopAsync(deadline.Token);
        Assert.Equal(0, queue.Deleted);
        Assert.Equal(2, f.Session.Count);
    }

    [Fact]
    public async Task Visibility_failure_cancels_possible_send_and_preserves_envelope()
    {
        using var f = new ProcessorFixture();
        f.Options.HeartbeatSeconds = 1;
        var queue = new TestQueue { FailHeartbeat = true };
        f.Session.OnSubmit = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(SubmitOutcome.Accepted, "ok"); };
        var envelope = Samples.Envelope();
        using var worker = new SenderService(queue, f.Ledger, f.Session, f.Processor, f.Gate, f.Options, NullLogger<SenderService>.Instance);
        queue.Add(envelope);
        await worker.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await f.Read(envelope))?.State != SubmissionState.Unknown) await Task.Delay(20, deadline.Token);
        await worker.StopAsync(deadline.Token);
        Assert.Equal(0, queue.Deleted);
        Assert.Equal(1, f.Session.Count);
    }

    private sealed class TestQueue : IEnvelopeQueue
    {
        private readonly Channel<QueueReceipt> _queue = Channel.CreateBounded<QueueReceipt>(10);
        public int Deleted;
        public bool FailHeartbeat { get; init; }
        public void Add(SmsEnvelope envelope) => _queue.Writer.TryWrite(new(
            System.Text.Json.JsonSerializer.Serialize(envelope, ContractJson.Default.SmsEnvelope), Guid.NewGuid().ToString()));
        public async Task<QueueReceipt?> ReceiveAsync(CancellationToken cancellationToken) => await _queue.Reader.ReadAsync(cancellationToken);
        public Task ExtendAsync(string handle, CancellationToken cancellationToken) => FailHeartbeat ? Task.FromException(new IOException("injected")) : Task.CompletedTask;
        public Task DeleteAsync(string handle, CancellationToken cancellationToken) { Interlocked.Increment(ref Deleted); return Task.CompletedTask; }
    }
}
