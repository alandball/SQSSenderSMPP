using Amazon.DynamoDBv2;
using Amazon.SQS;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sender.Aws;
using Sender.Core;
using Sender.Smpp;

var builder = WebApplication.CreateBuilder(args);
// A work PC can supply an untracked file. Environment variables and CLI retain precedence.
var configFile = Environment.GetEnvironmentVariable("SENDER_CONFIG_FILE");
if (!string.IsNullOrEmpty(configFile))
    builder.Configuration.AddJsonFile(Path.GetFullPath(configFile), optional: false, reloadOnChange: false)
        .AddEnvironmentVariables().AddCommandLine(args);

var sender = builder.Configuration.GetSection("Sender").Get<SenderOptions>() ?? new();
var aws = builder.Configuration.GetSection("Aws").Get<AwsOptions>() ?? new();
var smpp = builder.Configuration.GetSection("Smpp").Get<SmppOptions>() ?? new();
if (!sender.IsValid()) throw new InvalidOperationException("Invalid Sender options.");
aws.Validate(builder.Environment.IsDevelopment());
smpp.Validate(builder.Environment.IsDevelopment(), sender);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(sender.ShutdownSeconds + 5));
builder.Services.AddSingleton(sender);
builder.Services.AddSingleton(aws);
builder.Services.AddSingleton(smpp);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAmazonSQS>(_ => aws.CreateSqs());
builder.Services.AddSingleton<IAmazonDynamoDB>(_ => aws.CreateDynamo());
builder.Services.AddSingleton<IEnvelopeQueue, SqsEnvelopeQueue>();
builder.Services.AddSingleton<ISubmissionLedger, DynamoSubmissionLedger>();
builder.Services.AddSingleton<IDeliveryReceiptStore, DynamoDeliveryReceiptStore>();
builder.Services.AddSingleton<ISmppSession, InetlabSession>();
builder.Services.AddSingleton<SubmitGate>();
builder.Services.AddSingleton<SubmissionProcessor>();
builder.Services.AddSingleton<SenderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SenderService>());

var telemetry = builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("sqs-smpp-sender"));
telemetry.WithMetrics(m =>
{
    m.AddMeter(Telemetry.Name).AddAspNetCoreInstrumentation();
    if (builder.Configuration.GetValue<bool>("Telemetry:EnableOtlp")) m.AddOtlpExporter();
});
telemetry.WithTracing(t =>
{
    t.AddSource(Telemetry.Name).SetSampler(new TraceIdRatioBasedSampler(0.01));
    if (builder.Configuration.GetValue<bool>("Telemetry:EnableOtlp")) t.AddOtlpExporter();
});

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (SenderService worker) => worker.IsReady
    ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
await app.RunAsync();
