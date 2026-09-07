namespace Sender.Core;

public sealed class SenderOptions
{
    public int MaxActiveEnvelopes { get; set; } = 16;
    public int Window { get; set; } = 10;
    public int PerBindTps { get; set; } = 10;
    public int VisibilitySeconds { get; set; } = 120;
    public int HeartbeatSeconds { get; set; } = 30;
    public int MaxEnvelopeAgeSeconds { get; set; } = 900;
    public int LeaseSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
    public int RetryBaseMilliseconds { get; set; } = 1000;
    public int RetryMaxSeconds { get; set; } = 60;
    public int RetentionDays { get; set; } = 30;
    public int ShutdownSeconds { get; set; } = 105;
    public int PersistenceTimeoutSeconds { get; set; } = 10;

    public bool IsValid() => MaxActiveEnvelopes is >= 1 and <= 1000 && Window is >= 1 and <= 1000 &&
        MaxActiveEnvelopes >= Window && PerBindTps is >= 1 and <= 100000 &&
        VisibilitySeconds is >= 3 and <= 43200 && HeartbeatSeconds >= 1 && HeartbeatSeconds * 2 < VisibilitySeconds &&
        MaxEnvelopeAgeSeconds >= VisibilitySeconds && MaxEnvelopeAgeSeconds <= 3600 &&
        LeaseSeconds is >= 5 and <= 3600 && MaxAttempts is >= 1 and <= 100 &&
        RetryBaseMilliseconds >= 1 && RetryMaxSeconds is >= 1 and <= 3600 &&
        RetentionDays is >= 15 and <= 3650 && ShutdownSeconds is >= 1 and <= 110 &&
        PersistenceTimeoutSeconds is >= 1 and <= 30;
}
