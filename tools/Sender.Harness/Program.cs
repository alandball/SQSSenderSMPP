using Sender.Harness;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5081");
await using var simulator = new Simulator(2775);
await simulator.StartAsync();
var app = builder.Build();
app.MapGet("/health", () => Results.Ok());
app.MapGet("/stats", () => new { simulator.Scenario, simulator.Requests, simulator.Accepted, simulator.ReceiptAcks, simulator.Binds });
app.MapPost("/scenario/{scenario}", (string scenario) =>
{
    if (!Simulator.Scenarios.Contains(scenario)) return Results.BadRequest(new { allowed = Simulator.Scenarios });
    simulator.Scenario = scenario;
    return Results.Ok();
});
await app.RunAsync();
