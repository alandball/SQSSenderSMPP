using Microsoft.Extensions.Logging.Abstractions;
using Sender.Core;

namespace Sender.Tests;

internal sealed class MemoryLedger : ISubmissionLedger
{
    private readonly Dictionary<string, LedgerEntry> _records = [];
    private readonly Lock _lock = new();
    public bool FailReads { get; set; }
    public bool FailAcceptedWrites { get; set; }
    public Task<LedgerEntry?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (FailReads) throw new IOException("injected");
        lock (_lock) return Task.FromResult(_records.GetValueOrDefault(key));
    }
    public Task<bool> WriteAsync(LedgerEntry entry, long? expectedRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailAcceptedWrites && entry.State == SubmissionState.Accepted) throw new IOException("injected");
        lock (_lock)
        {
            var exists = _records.TryGetValue(entry.Key, out var existing);
            if (expectedRevision is null ? exists : !exists || existing!.Revision != expectedRevision) return Task.FromResult(false);
            _records[entry.Key] = entry;
            return Task.FromResult(true);
        }
    }
    public Task ProbeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FakeSession : ISmppSession
{
    public int Count;
    public bool IsBound { get; set; } = true;
    public int Segments { get; set; } = 1;
    public Func<int, CancellationToken, Task<SubmitResult>> OnSubmit { get; set; } = (i, _) => Task.FromResult(new SubmitResult(SubmitOutcome.Accepted, "ok", $"id-{i}"));
    public PreparedSms Prepare(SmsMessage sms) => new FakePrepared(Segments);
    public Task ConnectAsync(CancellationToken cancellationToken) { IsBound = true; return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken cancellationToken) { IsBound = false; return Task.CompletedTask; }
    public Task<SubmitResult> SubmitAsync(PreparedSms sms, int segment, CancellationToken cancellationToken)
    { Interlocked.Increment(ref Count); return OnSubmit(segment, cancellationToken); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private sealed class FakePrepared(int count) : PreparedSms(count, "fake-v1");
}

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(int seconds = 120) => Now = Now.AddSeconds(seconds);
}

internal sealed class ProcessorFixture : IDisposable
{
    public MemoryLedger Ledger { get; } = new();
    public FakeSession Session { get; } = new();
    public TestClock Clock { get; } = new();
    public SenderOptions Options { get; } = new() { PerBindTps = 100000, RetryBaseMilliseconds = 1, RetryMaxSeconds = 1 };
    public SubmitGate Gate { get; }
    public SubmissionProcessor Processor { get; }
    public ProcessorFixture()
    {
        Gate = new(Options);
        Processor = new(Ledger, Session, Gate, Options, Clock, NullLogger<SubmissionProcessor>.Instance);
    }
    public Task<bool> Process(SmsEnvelope envelope, CancellationToken ct = default) => Processor.ProcessAsync(envelope, envelope.Messages[0], ct);
    public Task<LedgerEntry?> Read(SmsEnvelope envelope) => Ledger.ReadAsync(EnvelopeContract.Key(envelope, envelope.Messages[0]), default);
    public void Dispose() => Gate.Dispose();
}

internal static class Samples
{
    public static SmsEnvelope Envelope(int count = 1) => new()
    {
        SchemaVersion = "1.0",
        EnvelopeId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        Messages = Enumerable.Range(0, count).Select(i => new SmsMessage
        {
            SmsId = Guid.NewGuid(),
            Destination = $"+1555000{i:0000}",
            Source = "LOCAL",
            Text = "Synthetic local test"
        }).ToArray()
    };
}
