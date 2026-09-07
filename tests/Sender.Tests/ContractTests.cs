using System.Text.Json;
using Sender.Core;

namespace Sender.Tests;

public sealed class ContractTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void Valid_contract_roundtrips(int count)
    {
        var envelope = Samples.Envelope(count);
        Assert.Equal(count, EnvelopeContract.Parse(JsonSerializer.Serialize(envelope, ContractJson.Default.SmsEnvelope)).Messages.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Invalid_envelope_size_is_poison(int count) => Assert.Throws<ContractException>(() =>
        EnvelopeContract.Parse(JsonSerializer.Serialize(Samples.Envelope(count), ContractJson.Default.SmsEnvelope)));

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{invalid}")]
    public void Malformed_contract_fails_closed(string json) => Assert.Throws<ContractException>(() => EnvelopeContract.Parse(json));

    [Fact]
    public void Duplicate_and_unknown_properties_are_rejected()
    {
        var json = JsonSerializer.Serialize(Samples.Envelope(), ContractJson.Default.SmsEnvelope);
        Assert.Throws<ContractException>(() => EnvelopeContract.Parse(json.Insert(1, "\"unknown\":1,")));
        Assert.Throws<ContractException>(() => EnvelopeContract.Parse(json.Insert(1, "\"schemaVersion\":\"1.0\",")));
    }

    [Fact]
    public void Duplicate_child_ids_are_rejected()
    {
        var envelope = Samples.Envelope();
        envelope = envelope with { Messages = [envelope.Messages[0], envelope.Messages[0]] };
        Assert.Throws<ContractException>(() => EnvelopeContract.Parse(JsonSerializer.Serialize(envelope, ContractJson.Default.SmsEnvelope)));
    }
}
