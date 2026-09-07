using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sender.Core;

public sealed record SmsEnvelope
{
    public required string SchemaVersion { get; init; }
    public required Guid EnvelopeId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? TenantId { get; init; }
    public string? Traceparent { get; init; }
    public required SmsMessage[] Messages { get; init; }
}

public sealed record SmsMessage
{
    public required Guid SmsId { get; init; }
    public required string Destination { get; init; }
    public required string Source { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset? ValidUntil { get; init; }
    public bool RequestDeliveryReceipt { get; init; } = true;
    public string? ProviderProfile { get; init; }
    public string? ClientReference { get; init; }
}

public static partial class EnvelopeContract
{
    public static SmsEnvelope Parse(string body)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(body, ContractJson.Strict.SmsEnvelope);
            if (envelope is null || envelope.SchemaVersion != "1.0" || envelope.EnvelopeId == Guid.Empty ||
                envelope.CreatedAt == default || envelope.Messages is not { Length: >= 1 and <= 50 } ||
                envelope.TenantId is not null && !NamePattern().IsMatch(envelope.TenantId) ||
                envelope.Traceparent is { Length: > 55 })
                throw new ContractException("invalid_envelope");
            var ids = new HashSet<Guid>();
            foreach (var sms in envelope.Messages)
            {
                // Structural errors poison the envelope. Business/provider errors reject only the child.
                if (sms is null || sms.SmsId == Guid.Empty || !ids.Add(sms.SmsId) ||
                    sms.Destination is null || !PhonePattern().IsMatch(sms.Destination) ||
                    sms.Source is not { Length: >= 1 and <= 21 } || sms.Text is not { Length: >= 1 and <= 2000 } ||
                    sms.ProviderProfile is not null && !NamePattern().IsMatch(sms.ProviderProfile) ||
                    sms.ClientReference is not null && sms.ClientReference.Length is < 1 or > 64)
                    throw new ContractException("invalid_child_contract");
            }
            return envelope;
        }
        catch (JsonException) { throw new ContractException("invalid_json"); }
    }

    public static string Key(SmsEnvelope envelope, SmsMessage sms) => $"{envelope.TenantId ?? "default"}/{sms.SmsId:D}";
    public static string Hash(SmsMessage sms) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(sms, ContractJson.Default.SmsMessage)));

    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

public sealed class ContractException(string reason) : Exception(reason);
public sealed class MessagePolicyException(string reason) : Exception(reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SmsEnvelope))]
[JsonSerializable(typeof(SmsMessage))]
[JsonSerializable(typeof(LedgerEntry))]
public partial class ContractJson : JsonSerializerContext
{
    public static ContractJson Strict { get; } = new(new JsonSerializerOptions
    {
        AllowDuplicateProperties = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    });
}
