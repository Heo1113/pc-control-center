using System.Diagnostics;
using System.Globalization;
using PcControlCenter.Basalt.Mock;

namespace PcControlCenter.FakeBasalt;

/// <summary>
/// basalt-fake wrapper|server|engine [--port N] [--ignore-ctrl-c] [--respawn]. Children share the console (no new
/// window), so Ctrl+C sent to it reaches all of them, as with the real Basalt.
/// </summary>
internal static class Program
{
    private static readonly ManualResetEventSlim Stop = new();

    private static int Main(string[] args)
    {
        var role = args.FirstOrDefault() ?? "";
        var ignore = args.Contains("--ignore-ctrl-c");
        var respawn = args.Contains("--respawn");
        var portIndex = Array.IndexOf(args, "--port");
        var port = portIndex >= 0 && portIndex + 1 < args.Length ? int.Parse(args[portIndex + 1], CultureInfo.InvariantCulture) : 0;
        var flags = args.Where(a => a is "--ignore-ctrl-c" or "--respawn").ToArray();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // never the default termination: end the way the role ends
            if (!ignore)
            {
                Stop.Set();
            }
        };

        switch (role)
        {
            case "wrapper":
            {
                using var server = Child("server", ["--port", port.ToString(CultureInfo.InvariantCulture), .. flags]);
                while (!Stop.Wait(200) && !server.HasExited)
                {
                }

                return 0;
            }

            case "server":
            {
                using var http = new LoopbackHttpServer(path => path == "/health"
                    ? new LoopbackResponse(200, "application/json", "{\"service\":\"basalt-fake\",\"loaded\":true}")
                    : new LoopbackResponse(200, "text/html; charset=utf-8", "<!doctype html><title>basalt-fake</title><p>basalt-fake</p>"), port);
                var engine = Child("engine", flags);
                while (!Stop.Wait(200))
                {
                    if (engine.HasExited && respawn)
                    {
                        engine.Dispose();
                        engine = Child("engine", flags);
                    }
                }

                engine.Dispose();
                return 0;
            }

            case "engine":
                Stop.Wait();
                return 0;
            default:
                Console.Error.WriteLine("usage: basalt-fake wrapper|server|engine [--port N] [--ignore-ctrl-c] [--respawn]");
                return 64;
        }
    }

    private static Process Child(string role, string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        info.ArgumentList.Add(role);
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        return Process.Start(info)!;
    }
}
