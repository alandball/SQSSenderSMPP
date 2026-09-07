using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Sender.Core;

public static class Telemetry
{
    public const string Name = "Sender";
    public static readonly Meter Meter = new(Name);
    public static readonly ActivitySource Activity = new(Name);
    public static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("sender.submission.outcomes");
    public static readonly Counter<long> Dedupe = Meter.CreateCounter<long>("sender.deduplicated");
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("sender.errors");
    public static readonly Counter<long> Deleted = Meter.CreateCounter<long>("sender.envelopes.deleted");
    public static readonly Histogram<double> SubmitLatency = Meter.CreateHistogram<double>("sender.submit.duration", "s");
    public static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("sender.envelopes.active");
}
