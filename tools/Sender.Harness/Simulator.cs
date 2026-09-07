using System.Collections.Concurrent;
using System.Net;
using Inetlab.SMPP;
using Inetlab.SMPP.Common;
using Inetlab.SMPP.Logging;
using Inetlab.SMPP.PDU;

namespace Sender.Harness;

public sealed class Simulator : IAsyncDisposable
{
    private readonly SmppServer _server;
    private readonly ConcurrentDictionary<long, Task> _responses = new();
    private readonly ConcurrentDictionary<string, int> _seen = new();
    private readonly CancellationTokenSource _stop = new();
    private long _requests;
    private long _accepted;
    private long _receipts;
    private long _binds;
    private string _scenario = "accept";

    public Simulator(int port)
    {
        LogManager.SetLoggerFactory(new NullLogFactory());
        _server = new SmppServer(new IPEndPoint(IPAddress.Loopback, port));
        _server.evClientBind += (_, _, bind) =>
        {
            if (bind.SystemId != "local" || bind.Password != "local" || Scenario == "bind-reject")
                bind.Response.Header.Status = CommandStatus.ESME_RBINDFAIL;
            else Interlocked.Increment(ref _binds);
        };
        _server.evClientSubmitSm += OnSubmit;
    }

    public string Scenario { get => Volatile.Read(ref _scenario); set => Volatile.Write(ref _scenario, value); }
    public long Requests => Interlocked.Read(ref _requests);
    public long Accepted => Interlocked.Read(ref _accepted);
    public long ReceiptAcks => Interlocked.Read(ref _receipts);
    public long Binds => Interlocked.Read(ref _binds);
    public static string[] Scenarios { get; } = ["accept", "permanent", "throttle-once", "timeout", "disconnect", "out-of-order", "dlr", "bind-reject"];

    public Task StartAsync() => _server.StartAsync();

    private void OnSubmit(object sender, SmppServerClient client, SubmitSm pdu)
    {
        var number = Interlocked.Increment(ref _requests);
        var scenario = Scenario;
        var response = pdu.Response;
        response.MessageId = $"local-{number}";
        if (scenario == "permanent") { response.Header.Status = CommandStatus.ESME_RINVDSTADR; return; }
        var signature = $"{pdu.DestinationAddress.Address}/{pdu.Concatenation?.SequenceNumber ?? 1}";
        if (scenario == "throttle-once" && _seen.AddOrUpdate(signature, 1, (_, count) => count + 1) == 1)
        { response.Header.Status = CommandStatus.ESME_RTHROTTLED; return; }
        Interlocked.Increment(ref _accepted);
        if (scenario == "timeout") { pdu.Response = null; return; }
        if (scenario is "disconnect" or "out-of-order" or "dlr")
        {
            pdu.Response = null;
            // Bound by the simulator request cap; no unbounded task accumulation under a faulty client.
            if (_responses.Count >= 128)
            {
                response.Header.Status = CommandStatus.ESME_RMSGQFUL;
                pdu.Response = response;
                return;
            }
            _responses[number] = RespondAsync(client, pdu, response, scenario, number);
        }
    }

    private async Task RespondAsync(SmppServerClient client, SubmitSm pdu, SubmitSmResp response, string scenario, long number)
    {
        // Ensure the task is registered before its finally removes it.
        await Task.Delay(10, _stop.Token);
        try
        {
            if (scenario == "disconnect") { await client.DisconnectAsync(); return; }
            if (scenario == "out-of-order") await Task.Delay(number % 2 == 0 ? 20 : 200, _stop.Token);
            if (scenario == "dlr")
            {
                var dlr = new DeliverSm
                {
                    SourceAddress = pdu.DestinationAddress,
                    DestinationAddress = pdu.SourceAddress,
                    MessageType = MessageTypes.SMSCDeliveryReceipt,
                    Receipt = new Receipt
                    {
                        MessageId = response.MessageId,
                        State = MessageState.Delivered,
                        Submitted = 1,
                        Delivered = 1,
                        SubmitDate = DateTime.UtcNow,
                        DoneDate = DateTime.UtcNow,
                        ErrorCode = "000"
                    }
                };
                var ack = await client.DeliverAsync(dlr).WaitAsync(TimeSpan.FromSeconds(10), _stop.Token);
                if (ack.Header.Status == CommandStatus.ESME_ROK) Interlocked.Increment(ref _receipts);
            }
            await client.SendResponseAsync(response, _stop.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Deliberate harness disconnects/timeouts. */ }
        finally { _responses.TryRemove(number, out _); }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _server.StopAsync();
        await Task.WhenAll(_responses.Values);
        _server.Dispose();
        _stop.Dispose();
    }
}
