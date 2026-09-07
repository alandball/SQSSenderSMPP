using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sender.Core;

public sealed class SenderService(
    IEnvelopeQueue queue, ISubmissionLedger ledger, ISmppSession session, SubmissionProcessor processor,
    SubmitGate gate, SenderOptions options, ILogger<SenderService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _admission = new(options.MaxActiveEnvelopes, options.MaxActiveEnvelopes);
    private readonly Channel<EnvelopeWork> _ready = Channel.CreateBounded<EnvelopeWork>(options.MaxActiveEnvelopes);
    private volatile bool _draining;
    private volatile bool _ledgerAvailable;
    public bool IsReady => !_draining && session.IsBound && _ledgerAvailable && !gate.IsPaused && _admission.CurrentCount > 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var deadline = new CancellationTokenSource();
        using var stopRegistration = stoppingToken.Register(() =>
        {
            _draining = true;
            deadline.CancelAfter(TimeSpan.FromSeconds(options.ShutdownSeconds));
        });
        var workers = Enumerable.Range(0, options.Window).Select(_ => ConsumeAsync(deadline.Token)).ToArray();
        var sessionLoop = MaintainSessionAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!session.IsBound || gate.IsPaused)
                {
                    await Task.Delay(250, stoppingToken);
                    continue;
                }
                await _admission.WaitAsync(stoppingToken);
                var admitted = false;
                try
                {
                    await ledger.ProbeAsync(stoppingToken);
                    _ledgerAvailable = true;
                    var receipt = await queue.ReceiveAsync(stoppingToken);
                    if (receipt is null) continue;
                    SmsEnvelope envelope;
                    try { envelope = EnvelopeContract.Parse(receipt.Body); }
                    catch (ContractException ex)
                    {
                        logger.LogWarning("Envelope retained for redrive: {Reason}", ex.Message);
                        Telemetry.Errors.Add(1, new KeyValuePair<string, object?>("kind", "contract"));
                        continue;
                    }
                    var work = new EnvelopeWork(receipt.Handle, envelope, deadline.Token, options.MaxEnvelopeAgeSeconds);
                    work.Heartbeat = HeartbeatAsync(work);
                    if (!_ready.Writer.TryWrite(work)) throw new InvalidOperationException("Admission accounting violated.");
                    Telemetry.Active.Add(1);
                    admitted = true;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _ledgerAvailable = false;
                    logger.LogWarning("Intake dependency unavailable ({ErrorType})", ex.GetType().Name);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                finally { if (!admitted) _admission.Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _draining = true;
            while (_admission.CurrentCount != options.MaxActiveEnvelopes && !deadline.IsCancellationRequested)
                await Task.Delay(50);
            _ready.Writer.TryComplete();
            await Task.WhenAll(workers);
            await sessionLoop;
            using var unbind = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await session.DisconnectAsync(unbind.Token); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { logger.LogWarning("Unbind ended with {ErrorType}", ex.GetType().Name); }
        }
    }

    private async Task MaintainSessionAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (session.IsBound)
                {
                    failures = 0;
                    await Task.Delay(250, cancellationToken);
                    continue;
                }
                try { await session.ConnectAsync(cancellationToken); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { logger.LogWarning("SMPP bind unavailable ({ErrorType})", ex.GetType().Name); }
                if (!session.IsBound)
                {
                    var seconds = Math.Max(1, Random.Shared.NextDouble() * Math.Min(60, Math.Pow(2, Math.Min(++failures, 6))));
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        // Round robin at logical-SMS boundaries: exactly one scheduled item per admitted envelope.
        await foreach (var work in _ready.Reader.ReadAllAsync())
        {
            var finished = false;
            try
            {
                work.Cancellation.Token.ThrowIfCancellationRequested();
                var terminal = await processor.ProcessAsync(work.Envelope, work.Envelope.Messages[work.Index], work.Cancellation.Token);
                work.AllTerminal &= terminal;
                work.Index++;
                finished = work.Index == work.Envelope.Messages.Length;
                if (finished && work.AllTerminal && !work.Cancellation.IsCancellationRequested)
                {
                    await queue.DeleteAsync(work.Handle, work.Cancellation.Token);
                    Telemetry.Deleted.Add(1);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                finished = true;
                Telemetry.Errors.Add(1, new KeyValuePair<string, object?>("kind", "processing"));
                logger.LogWarning("Envelope left undeleted ({ErrorType})", ex.GetType().Name);
            }
            if (finished || cancellationToken.IsCancellationRequested)
            {
                await work.Cancellation.CancelAsync();
                await work.Heartbeat;
                work.Cancellation.Dispose();
                Telemetry.Active.Add(-1);
                _admission.Release();
            }
            else if (!_ready.Writer.TryWrite(work))
                throw new InvalidOperationException("Admission accounting violated.");
        }
    }

    private async Task HeartbeatAsync(EnvelopeWork work)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.HeartbeatSeconds));
            while (await timer.WaitForNextTickAsync(work.Cancellation.Token))
                await queue.ExtendAsync(work.Handle, work.Cancellation.Token);
        }
        catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Visibility ownership lost ({ErrorType})", ex.GetType().Name);
            Telemetry.Errors.Add(1, new KeyValuePair<string, object?>("kind", "visibility"));
            // Stop new sends on this receipt. A send already started will become UNKNOWN if needed.
            await work.Cancellation.CancelAsync();
        }
    }

    private sealed class EnvelopeWork(string handle, SmsEnvelope envelope, CancellationToken deadline, int maxAgeSeconds)
    {
        public string Handle { get; } = handle;
        public SmsEnvelope Envelope { get; } = envelope;
        public int Index { get; set; }
        public bool AllTerminal { get; set; } = true;
        public CancellationTokenSource Cancellation { get; } = CreateCancellation(deadline, maxAgeSeconds);
        public Task Heartbeat { get; set; } = Task.CompletedTask;
        private static CancellationTokenSource CreateCancellation(CancellationToken deadline, int seconds)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(deadline);
            source.CancelAfter(TimeSpan.FromSeconds(seconds));
            return source;
        }
    }

    public override void Dispose()
    {
        _admission.Dispose();
        base.Dispose();
    }
}
