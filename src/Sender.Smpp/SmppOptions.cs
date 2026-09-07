using System.Net;
using Sender.Core;

namespace Sender.Smpp;

public sealed class SmppOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 2775;
    public string SystemId { get; set; } = "";
    public string Password { get; set; } = "";
    public string? PasswordFile { get; set; }
    public string? LicenseFile { get; set; }
    public string SystemType { get; set; } = "";
    public byte InterfaceVersion { get; set; } = 0x34;
    public string Profile { get; set; } = "default";
    public bool UseTls { get; set; } = true;
    public bool PrivateTransportApproved { get; set; }
    public int ResponseTimeoutSeconds { get; set; } = 10;
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int EnquireLinkSeconds { get; set; } = 30;
    public byte SourceTon { get; set; } = 5;
    public byte SourceNpi { get; set; }
    public byte DestinationTon { get; set; } = 1;
    public byte DestinationNpi { get; set; } = 1;
    public string Concatenation { get; set; } = "UDH16bit";
    public int MaxSegments { get; set; } = 32;
    public uint[] RetryableStatuses { get; set; } = [0x14, 0x58];
    public uint[] PermanentStatuses { get; set; } = [0x01, 0x0A, 0x0B, 0x0C, 0x15];

    public void Validate(bool development, SenderOptions sender)
    {
        if (PasswordFile is not null) Password = File.ReadAllText(PasswordFile).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(SystemId) || SystemId.Length > 15 ||
            string.IsNullOrEmpty(Password) || Password.Length > 8 || SystemType.Length > 12 ||
            Port is < 1 or > 65535 || InterfaceVersion is not (0x33 or 0x34 or 0x50) ||
            string.IsNullOrWhiteSpace(Profile) || Profile.Length > 64 ||
            ResponseTimeoutSeconds is < 1 or > 120 || ConnectTimeoutSeconds is < 1 or > 60 ||
            EnquireLinkSeconds is < 1 or > 300 || sender.LeaseSeconds <= ResponseTimeoutSeconds + sender.PersistenceTimeoutSeconds ||
            MaxSegments is < 1 or > 255 || SourceTon > 6 || DestinationTon > 6 || SourceNpi > 18 || DestinationNpi > 18 ||
            Concatenation is not ("UDH8bit" or "UDH16bit" or "SAR") ||
            RetryableStatuses is null || PermanentStatuses is null || RetryableStatuses.Intersect(PermanentStatuses).Any() ||
            RetryableStatuses.Concat(PermanentStatuses).Any(s => s == 0 || s >= 0x10000000))
            throw new InvalidOperationException("Invalid SMPP provider profile or timers.");
        var loopback = Host == "localhost" || IPAddress.TryParse(Host, out var ip) && IPAddress.IsLoopback(ip);
        if (!UseTls && !(development && loopback) && !PrivateTransportApproved)
            throw new InvalidOperationException("SMPP requires TLS or an explicitly approved private transport.");
        if (!development && string.IsNullOrWhiteSpace(LicenseFile))
            throw new InvalidOperationException("Work mode requires an injected Inetlab license file.");
    }
}
