using System.Diagnostics;
using Sender.Core;

namespace Sender.Tests;

public sealed class RateLimitTests
{
    [Fact]
    public async Task Slow_persistence_cannot_accumulate_permits_into_a_submit_burst()
    {
        using var gate = new SubmitGate(new() { PerBindTps = 20 });
        using var first = await gate.AcquireAsync(default);
        var second = gate.AcquireAsync(default);
        await Task.Delay(100); // Models a slow conditional SEND_STARTED write.
        Assert.False(second.IsCompleted);
        var start = Stopwatch.GetTimestamp();
        first.Dispose();
        using var next = await second.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(Stopwatch.GetElapsedTime(start) >= TimeSpan.FromMilliseconds(40));
    }
}
