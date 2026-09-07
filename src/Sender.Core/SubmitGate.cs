using System.Diagnostics;

namespace Sender.Core;

// A one-token paced bucket avoids bursts. The window is enforced separately by the fixed worker count.
public sealed class SubmitGate(SenderOptions options) : IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private long _lastStart;
    private double _effectiveTps = options.PerBindTps;
    private long _blockedUntil;

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                var rate = Volatile.Read(ref _effectiveTps);
                var next = Math.Max(_lastStart + (long)(Stopwatch.Frequency / rate), Interlocked.Read(ref _blockedUntil));
                var delay = TimeSpan.FromSeconds((next - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                if (delay <= TimeSpan.Zero) break;
                await Task.Delay(delay, cancellationToken);
            }
            // Hold through the conditional write and actual submit initiation. Otherwise uneven
            // datastore latency can bunch previously granted permits into an over-TPS burst.
            return new Permit(this);
        }
        catch { _mutex.Release(); throw; }
    }

    public void Observe(SubmitOutcome outcome)
    {
        if (outcome == SubmitOutcome.Retryable)
        {
            Volatile.Write(ref _effectiveTps, Math.Max(1, Volatile.Read(ref _effectiveTps) / 2));
            Interlocked.Exchange(ref _blockedUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency);
        }
        else if (outcome == SubmitOutcome.Accepted)
            Volatile.Write(ref _effectiveTps, Math.Min(options.PerBindTps, Volatile.Read(ref _effectiveTps) + 0.1));
    }

    public bool IsPaused => Stopwatch.GetTimestamp() < Interlocked.Read(ref _blockedUntil);
    public void Dispose() => _mutex.Dispose();

    private sealed class Permit(SubmitGate gate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            gate._lastStart = Stopwatch.GetTimestamp();
            gate._mutex.Release();
        }
    }
}
