namespace Sender.Core;

public sealed record QueueReceipt(string Body, string Handle);

public interface IEnvelopeQueue
{
    Task<QueueReceipt?> ReceiveAsync(CancellationToken cancellationToken);
    Task ExtendAsync(string handle, CancellationToken cancellationToken);
    Task DeleteAsync(string handle, CancellationToken cancellationToken);
}

public interface ISubmissionLedger
{
    Task<LedgerEntry?> ReadAsync(string key, CancellationToken cancellationToken);
    // expectedRevision=null means insert only. A false result NEVER grants ownership.
    Task<bool> WriteAsync(LedgerEntry entry, long? expectedRevision, CancellationToken cancellationToken);
    Task ProbeAsync(CancellationToken cancellationToken);
}

public abstract class PreparedSms(int segmentCount, string profileHash)
{
    public int SegmentCount { get; } = segmentCount;
    public string ProfileHash { get; } = profileHash;
}

public enum SubmitOutcome { Accepted, PermanentRejected, Retryable, Unknown }
public sealed record SubmitResult(SubmitOutcome Outcome, string Status, string? MessageId = null);

public interface ISmppSession : IAsyncDisposable
{
    bool IsBound { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    PreparedSms Prepare(SmsMessage sms);
    Task<SubmitResult> SubmitAsync(PreparedSms sms, int segment, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

public enum SubmissionState { Claimed, SendStarted, Retryable, RetryExhausted, Accepted, PermanentRejected, Unknown }
public enum SegmentState { Pending, SendStarted, Accepted, PermanentRejected, Unknown }

public sealed record SegmentProgress(SegmentState State, string? MessageId = null, string? Status = null);

public sealed record LedgerEntry
{
    public required string Key { get; init; }
    public required string PayloadHash { get; init; }
    public required string ProfileHash { get; init; }
    public required Guid EnvelopeId { get; init; }
    public required string Owner { get; init; }
    public long Revision { get; init; }
    public SubmissionState State { get; init; }
    public required SegmentProgress[] Segments { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset LeaseUntil { get; init; }
    public DateTimeOffset NextAttemptAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public long ExpiresAt { get; init; }
    public string? Reason { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTerminal => State is SubmissionState.Accepted or SubmissionState.PermanentRejected or SubmissionState.Unknown;
}
