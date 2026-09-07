using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Inetlab.SMPP;
using Inetlab.SMPP.Builders;
using Inetlab.SMPP.Common;
using Inetlab.SMPP.Diagnostics;
using Inetlab.SMPP.Logging;
using Inetlab.SMPP.PDU;
using Microsoft.Extensions.Logging;
using Sender.Core;

namespace Sender.Smpp;

public sealed class InetlabSession : ISmppSession
{
    private readonly SmppClient _client;
    private readonly SmppOptions _options;
    private readonly IDeliveryReceiptStore _receipts;
    private readonly ILogger<InetlabSession> _logger;
    private readonly Channel<ReceiptWork> _receiptQueue = Channel.CreateBounded<ReceiptWork>(256);
    private readonly CancellationTokenSource _receiptStop = new();
    private readonly Task _receiptPump;
    private readonly string _profileHash;

    public InetlabSession(SmppOptions options, SenderOptions sender, IDeliveryReceiptStore receipts, ILogger<InetlabSession> logger)
    {
        _options = options;
        _receipts = receipts;
        _logger = logger;
        // Vendor PDU logging/activity payloads can contain credentials and personal data.
        LogManager.SetLoggerFactory(new NullLogFactory());
        if (options.LicenseFile is not null)
        {
            using var license = File.OpenRead(options.LicenseFile);
            if (!LicenseManager.SetLicense(license) || LicenseManager.IsTrial)
                throw new InvalidOperationException("The configured Inetlab production license is not valid.");
        }
        _client = new SmppClient
        {
            ConnectionRecovery = false,
            InterfaceVersion = options.InterfaceVersion,
            SystemType = options.SystemType,
            ResponseTimeout = TimeSpan.FromSeconds(options.ResponseTimeoutSeconds),
            ConnectionTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds),
            EnquireLinkInterval = TimeSpan.FromSeconds(options.EnquireLinkSeconds),
            SendQueueLimit = sender.Window,
            ReceivedRequestQueueLimit = 256,
            SendSpeedLimit = LimitRate.NoLimit,
            ActivityDiagnosticsMode = ActivityDiagnosticsMode.Disabled,
            EnabledSslProtocols = options.UseTls ? SslProtocols.Tls12 | SslProtocols.Tls13 : SslProtocols.None
        };
        _client.evServerCertificateValidation += (_, args) => args.Accepted = args.SslPolicyErrors == SslPolicyErrors.None;
        _client.evDeliverSm += OnDeliver;
        _profileHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"inetlab-2.9.39/gsm-ucs2-v1/{options.Profile}/{options.InterfaceVersion}/{options.SourceTon}/{options.SourceNpi}/{options.DestinationTon}/{options.DestinationNpi}/{options.Concatenation}")));
        _receiptPump = PersistReceiptsAsync();
    }

    public bool IsBound => _client.Status == ConnectionStatus.Bound;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (IsBound) return;
        try
        {
            if (_client.Status != ConnectionStatus.Closed) await _client.DisconnectAsync().WaitAsync(cancellationToken);
            if (!await _client.ConnectAsync(_options.Host, _options.Port).WaitAsync(cancellationToken)) return;
            var response = await _client.BindAsync(_options.SystemId, _options.Password, ConnectionMode.Transceiver).WaitAsync(cancellationToken);
            if (response.Header.Status != CommandStatus.ESME_ROK)
                await _client.DisconnectAsync().WaitAsync(cancellationToken);
        }
        catch
        {
            await CloseConnectionAsync();
            throw;
        }
    }

    public PreparedSms Prepare(SmsMessage sms)
    {
        if (sms.ProviderProfile is not null && sms.ProviderProfile != _options.Profile)
            throw new MessagePolicyException("unsupported_profile");
        if (sms.Text.Any(char.IsSurrogate) || sms.Text.Contains('\0'))
            throw new MessagePolicyException("unsupported_unicode");
        if (!sms.Source.All(c => c is >= ' ' and <= '~') ||
            _options.SourceTon == 5 && (sms.Source.Length > 11 || !sms.Source.All(char.IsAsciiLetterOrDigit)) ||
            _options.SourceTon != 5 && !sms.Source.TrimStart('+').All(char.IsAsciiDigit))
            throw new MessagePolicyException("invalid_source");
        var coding = sms.Text.All(c => GsmBasic.Contains(c) || GsmExtension.Contains(c)) ? DataCodings.Default : DataCodings.UCS2;
        var reference = (ushort)((sms.SmsId.ToByteArray()[0] << 8) | sms.SmsId.ToByteArray()[1]);
        var builder = SMS.ForSubmit().From(sms.Source.TrimStart('+'), (AddressTON)_options.SourceTon, (AddressNPI)_options.SourceNpi)
            .To(sms.Destination.TrimStart('+'), (AddressTON)_options.DestinationTon, (AddressNPI)_options.DestinationNpi)
            .Text(sms.Text).Coding(coding)
            .Concatenation(Enum.Parse<ConcatenationType>(_options.Concatenation), reference)
            .Set(pdu => pdu.SMSCReceipt = sms.RequestDeliveryReceipt ? SMSCDeliveryReceipt.SuccessOrFailure : SMSCDeliveryReceipt.NotRequested);
        if (sms.ValidUntil is not null) builder = builder.ExpireAt(sms.ValidUntil.Value.UtcDateTime);
        var pdus = builder.Create(_client);
        if (pdus.Length == 0 || pdus.Length > _options.MaxSegments) throw new MessagePolicyException("too_many_segments");
        return new InetlabPrepared(pdus, _profileHash);
    }

    public async Task<SubmitResult> SubmitAsync(PreparedSms sms, int segment, CancellationToken cancellationToken)
    {
        if (!IsBound) return new(SubmitOutcome.Retryable, "not_bound_before_submit");
        try
        {
            // Exactly one PDU per call. Never use SubmitWithRepeatAsync, including on timeout.
            var response = await _client.SubmitAsync(((InetlabPrepared)sms).Pdus[segment]).WaitAsync(cancellationToken);
            var result = Classify((uint)response.Header.Status, response.MessageId);
            if (result.Outcome == SubmitOutcome.Unknown) await CloseConnectionAsync();
            return result;
        }
        catch
        {
            // Stop a queued/in-flight library task before freeing its application window slot.
            await CloseConnectionAsync();
            return new(SubmitOutcome.Unknown, "connection_or_cancellation");
        }
    }

    public SubmitResult Classify(uint status, string? messageId)
    {
        if (status == 0) return !string.IsNullOrWhiteSpace(messageId) && messageId.Length <= 128
            ? new(SubmitOutcome.Accepted, "ok", messageId.Trim()) : new(SubmitOutcome.Unknown, "missing_message_id");
        if (_options.RetryableStatuses.Contains(status)) return new(SubmitOutcome.Retryable, $"smpp_{status:X8}");
        if (_options.PermanentStatuses.Contains(status)) return new(SubmitOutcome.PermanentRejected, $"smpp_{status:X8}");
        // Unmapped carrier statuses and library-local errors require reconciliation/profile review.
        return new(SubmitOutcome.Unknown, "unmapped_or_local_status");
    }

    private void OnDeliver(object sender, DeliverSm pdu)
    {
        if (pdu.MessageType != MessageTypes.SMSCDeliveryReceipt)
        {
            pdu.Response.Header.Status = CommandStatus.ESME_RINVCMDID;
            return;
        }
        try
        {
            var receipt = pdu.Receipt;
            if (receipt is null || string.IsNullOrWhiteSpace(receipt.MessageId) || receipt.MessageId.Length > 128 || !Enum.IsDefined(receipt.State))
                throw new InvalidDataException();
            var state = receipt.State.ToString();
            var isFinal = state is "Delivered" or "Expired" or "Deleted" or "Undeliverable" or "Rejected";
            var response = pdu.Response;
            pdu.Response = null; // Ack only after durable storage; never async void in vendor callbacks.
            if (!_receiptQueue.Writer.TryWrite(new(new(_options.Profile, receipt.MessageId.Trim(), state, isFinal, DateTimeOffset.UtcNow), response)))
            {
                response.Header.Status = CommandStatus.ESME_RMSGQFUL;
                pdu.Response = response;
            }
        }
        catch
        {
            if (pdu.Response is not null) pdu.Response.Header.Status = CommandStatus.ESME_RINVMSGLEN;
            Telemetry.Errors.Add(1, new KeyValuePair<string, object?>("kind", "dlr_parse"));
        }
    }

    private async Task PersistReceiptsAsync()
    {
        try
        {
            await foreach (var work in _receiptQueue.Reader.ReadAllAsync(_receiptStop.Token))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_receiptStop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await _receipts.StoreAsync(work.Receipt, deadline.Token);
                    await _client.SendResponseAsync(work.Response, deadline.Token);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning("DLR persistence/ack failed ({ErrorType}); carrier retry required", ex.GetType().Name);
                    Telemetry.Errors.Add(1, new KeyValuePair<string, object?>("kind", "dlr_persist"));
                }
            }
        }
        catch (OperationCanceledException) when (_receiptStop.IsCancellationRequested) { }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (IsBound) await _client.UnbindAsync().WaitAsync(cancellationToken);
        await _client.DisconnectAsync().WaitAsync(cancellationToken);
    }

    private async Task CloseConnectionAsync()
    {
        try { await _client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Socket failure already implies UNKNOWN. */ }
    }

    public async ValueTask DisposeAsync()
    {
        _receiptQueue.Writer.TryComplete();
        _receiptStop.CancelAfter(TimeSpan.FromSeconds(5));
        await _receiptPump;
        _receiptStop.Dispose();
        _client.Dispose();
    }

    private sealed class InetlabPrepared(SubmitSm[] pdus, string profileHash) : PreparedSms(pdus.Length, profileHash)
    {
        public SubmitSm[] Pdus { get; } = pdus;
    }
    private sealed record ReceiptWork(DeliveryReceipt Receipt, DeliverSmResp Response);
    private const string GsmBasic = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string GsmExtension = "\f^{}\\[~]|€";
}
