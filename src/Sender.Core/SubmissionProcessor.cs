using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sender.Core;

public sealed class SubmissionProcessor(
    ISubmissionLedger ledger, ISmppSession session, SubmitGate gate, SenderOptions options,
    TimeProvider time, ILogger<SubmissionProcessor> logger)
{
    public async Task<bool> ProcessAsync(SmsEnvelope envelope, SmsMessage sms, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.Activity.StartActivity("sms.submit");
        var key = EnvelopeContract.Key(envelope, sms);
        var hash = EnvelopeContract.Hash(sms);
        var now = time.GetUtcNow();
        var existing = await ledger.ReadAsync(key, cancellationToken);
        if (existing is not null)
        {
            if (existing.PayloadHash != hash)
                throw new ContractException("id_payload_conflict");
            if (existing.IsTerminal)
            {
                Telemetry.Dedupe.Add(1);
                return true;
            }
            if (existing.State == SubmissionState.RetryExhausted) return false; // Preserve source for native DLQ redrive.
            if (existing.LeaseUntil > now || existing.NextAttemptAt > now) return false;
            if (existing.State == SubmissionState.SendStarted)
            {
                var unknown = existing with
                {
                    State = SubmissionState.Unknown,
                    Reason = "expired_send_started",
                    Segments = existing.Segments.Select(s => s.State == SegmentState.SendStarted
                        ? s with { State = SegmentState.Unknown, Status = "expired_send_started" } : s).ToArray()
                };
                return await SaveAsync(existing, unknown, cancellationToken);
            }
        }

        PreparedSms? prepared = null;
        string? rejection = null;
        try { prepared = session.Prepare(sms); }
        catch (MessagePolicyException ex) { rejection = ex.Message; }
        if (sms.ValidUntil <= now) rejection = "expired";

        var entry = existing is null ? new LedgerEntry
        {
            Key = key,
            PayloadHash = hash,
            ProfileHash = prepared?.ProfileHash ?? "rejected",
            EnvelopeId = envelope.EnvelopeId,
            Owner = Guid.NewGuid().ToString("N"),
            State = SubmissionState.Claimed,
            Revision = 0,
            Segments = Enumerable.Range(0, prepared?.SegmentCount ?? 0).Select(_ => new SegmentProgress(SegmentState.Pending)).ToArray(),
            LeaseUntil = now.AddSeconds(options.LeaseSeconds),
            UpdatedAt = now,
            ExpiresAt = (sms.ValidUntil > now ? sms.ValidUntil.Value : now).AddDays(options.RetentionDays).ToUnixTimeSeconds()
        } : existing with
        {
            Owner = Guid.NewGuid().ToString("N"),
            Revision = existing.Revision + 1,
            State = SubmissionState.Claimed,
            LeaseUntil = now.AddSeconds(options.LeaseSeconds),
            UpdatedAt = now
        };
        if (existing is not null && prepared is not null &&
            (existing.ProfileHash != prepared.ProfileHash || existing.Segments.Length != prepared.SegmentCount))
            throw new ContractException("encoding_profile_changed");
        if (!await ledger.WriteAsync(entry, existing?.Revision, cancellationToken)) return false;

        if (rejection is not null)
            return await SaveAsync(entry, entry with { State = SubmissionState.PermanentRejected, Reason = rejection }, cancellationToken);

        for (var i = 0; i < entry.Segments.Length; i++)
        {
            if (entry.Segments[i].State == SegmentState.Accepted) continue;
            using var submissionPermit = await gate.AcquireAsync(cancellationToken);
            if (!session.IsBound) return false; // CLAIMED is safe to reclaim after its lease.
            if (sms.ValidUntil <= time.GetUtcNow())
                return await SaveAsync(entry, entry with { State = SubmissionState.PermanentRejected, Reason = "expired" }, cancellationToken);

            var started = entry with
            {
                State = SubmissionState.SendStarted,
                Attempts = entry.Attempts + 1,
                LeaseUntil = time.GetUtcNow().AddSeconds(options.LeaseSeconds),
                Segments = Replace(entry.Segments, i, new(SegmentState.SendStarted))
            };
            // SaveAsync returns the CAS result; the local revision advances only after success.
            if (!await SaveAsync(entry, started, cancellationToken)) return false;
            entry = started with { Revision = entry.Revision + 1 };
            SubmitResult result;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var submission = session.SubmitAsync(prepared!, i, cancellationToken);
                submissionPermit.Dispose(); // The response waits outside the pacing lock.
                result = await submission;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Any exception beyond SEND_STARTED is ambiguous, including cancellation.
                result = new(SubmitOutcome.Unknown, "submit_exception");
            }
            finally { submissionPermit.Dispose(); }
            Telemetry.SubmitLatency.Record(stopwatch.Elapsed.TotalSeconds);
            gate.Observe(result.Outcome);

            var next = result.Outcome switch
            {
                SubmitOutcome.Accepted => entry with
                {
                    State = i == entry.Segments.Length - 1 ? SubmissionState.Accepted : SubmissionState.Claimed,
                    Segments = Replace(entry.Segments, i, new(SegmentState.Accepted, result.MessageId, result.Status))
                },
                SubmitOutcome.PermanentRejected => entry with
                {
                    State = SubmissionState.PermanentRejected,
                    Reason = result.Status,
                    Segments = Replace(entry.Segments, i, new(SegmentState.PermanentRejected, Status: result.Status))
                },
                SubmitOutcome.Retryable => entry with
                {
                    State = entry.Attempts - i >= options.MaxAttempts ? SubmissionState.RetryExhausted : SubmissionState.Retryable,
                    Reason = entry.Attempts - i >= options.MaxAttempts ? "retry_exhausted" : result.Status,
                    LeaseUntil = time.GetUtcNow(),
                    NextAttemptAt = time.GetUtcNow().AddMilliseconds(Random.Shared.NextDouble() * Math.Min(
                        options.RetryMaxSeconds * 1000.0, options.RetryBaseMilliseconds * Math.Pow(2, Math.Min(entry.Attempts, 20)))),
                    Segments = Replace(entry.Segments, i, new(SegmentState.Pending, Status: result.Status))
                },
                _ => entry with
                {
                    State = SubmissionState.Unknown,
                    Reason = result.Status,
                    Segments = Replace(entry.Segments, i, new(SegmentState.Unknown, Status: result.Status))
                }
            };
            // Shutdown must not erase a received response. Persistence has its own short deadline.
            using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(options.PersistenceTimeoutSeconds));
            if (!await SaveAsync(entry, next, persistence.Token)) return false;
            entry = next with { Revision = entry.Revision + 1 };
            if (entry.IsTerminal) return true;
            if (entry.State is SubmissionState.Retryable or SubmissionState.RetryExhausted) return false;
        }
        return entry.IsTerminal;
    }

    private async Task<bool> SaveAsync(LedgerEntry before, LedgerEntry after, CancellationToken cancellationToken)
    {
        var saved = await ledger.WriteAsync(after with { Revision = before.Revision + 1, UpdatedAt = time.GetUtcNow() }, before.Revision, cancellationToken);
        if (saved && after.IsTerminal)
        {
            Telemetry.Outcomes.Add(1, new KeyValuePair<string, object?>("state", after.State.ToString()));
            if (after.State == SubmissionState.Unknown)
                logger.LogWarning("Submission quarantined for reconciliation: {Reason}", after.Reason);
        }
        return saved;
    }

    private static SegmentProgress[] Replace(SegmentProgress[] segments, int index, SegmentProgress value)
    {
        var copy = (SegmentProgress[])segments.Clone();
        copy[index] = value;
        return copy;
    }
}
