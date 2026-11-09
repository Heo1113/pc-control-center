using LocalAiPerf.MockStrata;

// Test tool: serves synthetic Strata-shaped responses so the app can be tried without Strata.
// Usage: dotnet run --project tests/LocalAiPerf.MockStrata -- [--port 18080] [--mode V1|Legacy|StatusOnly|...]
var port = 18080;
var mode = MockMode.V1;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p))
    {
        port = p;
    }
    else if (args[i] == "--mode" && Enum.TryParse<MockMode>(args[i + 1], ignoreCase: true, out var m))
    {
        mode = m;
    }
}

await using var server = new MockStrataServer(port, mode);
server.Start();
Console.WriteLine($"Mock Strata ({mode}) listening on {server.BaseUrl} - synthetic data, test use only. Press Ctrl+C to stop.");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

var random = new Random(1);
try
{
    while (!stop.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(4), stop.Token);
        server.CompleteRequest(promptTokens: random.Next(500, 4000), outputTokens: random.Next(64, 600), decodeTokPerSec: 35 + random.NextDouble() * 10);
    }
}
catch (OperationCanceledException)
{
}
