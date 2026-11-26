using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Iron.App.Presentation;
using Iron.ProcessIdentity;
using Iron.Stage2;

namespace Iron.App.Hosting;

/// <summary>
/// --probe-real DIR: starts the Stage 2 readers without a window, waits, and writes what they read into DIR/probe.json —
/// the frame the window would show, the state of every source, the identities of the running processes (redacted
/// command lines only) and IRON's own CPU time, memory and handles. For CI and for the user-PC verification.
/// </summary>
internal static partial class RealProbe
{
    public static Stage2Options OptionsOf(AppOptions o) => new()
    {
        HwInfoUrl = o.HwInfoUrl.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : new Uri(o.HwInfoUrl),
    };

    public static int Run(AppOptions options)
    {
        var dir = Path.GetFullPath(options.ProbeRealTo!);
        Directory.CreateDirectory(dir);
        var me = Process.GetCurrentProcess();
        var cpu0 = me.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var samples = new List<object>();
        using var collector = new Stage2Collector(OptionsOf(options));
        collector.Start();
        Stage2Frame frame;
        do
        {
            Thread.Sleep(1000);
            frame = collector.Latest(DateTimeOffset.Now);
            me.Refresh();
            samples.Add(new
            {
                t = Math.Round(wall.Elapsed.TotalSeconds, 1),
                phase = frame.Phase.ToString(),
                gpus = frame.Gpus.Count,
                processes = frame.Processes.Count,
                cpuLoad = frame.Cpu.LoadPercent,
                tctlC = frame.Cpu.TctlC,
                wireView = frame.WireView.Completeness,
                privateMb = Math.Round(me.PrivateMemorySize64 / 1048576.0, 1),
                handles = me.HandleCount,
                gdi = GetGuiResources(me.Handle, 0),
            });
        }
        while (wall.Elapsed < TimeSpan.FromSeconds(options.ProbeSeconds));

        me.Refresh();
        var cpuPct = (me.TotalProcessorTime - cpu0).TotalMilliseconds / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
        var telemetry = RealTelemetrySource.Map(frame);
        var identities = new List<object>();
        if (collector.Resolver is { } resolver)
        {
            foreach (var e in resolver.TakeSnapshot())
            {
                if (resolver.TryGet(e.Key, out var info))
                {
                    identities.Add(Describe(info));
                }
            }
        }

        var report = new
        {
            generated = DateTimeOffset.Now,
            os = RuntimeInformation.OSDescription,
            uiCulture = System.Globalization.CultureInfo.CurrentUICulture.Name,
            elevated = Environment.IsPrivilegedProcess,
            seconds = options.ProbeSeconds,
            sources = frame.Sources.Select(s => new { s.Name, State = s.State.ToString(), s.Detail }),
            stats = collector.Stats,
            iron = new
            {
                cpuPercent = Math.Round(cpuPct, 3),
                privateMb = Math.Round(me.PrivateMemorySize64 / 1048576.0, 1),
                workingSetMb = Math.Round(me.WorkingSet64 / 1048576.0, 1),
                handles = me.HandleCount,
                threads = me.Threads.Count,
                gdi = GetGuiResources(me.Handle, 0),
                user = GetGuiResources(me.Handle, 1),
            },
            samples,
            frame = new
            {
                phase = telemetry.Phase.ToString(),
                gpus = telemetry.Gpus,
                cpu = telemetry.Cpu,
                ram = telemetry.Ram,
                ssd = telemetry.Ssd,
                wireView = telemetry.WireView,
                flux = telemetry.Flux,
                processes = frame.Processes.Select(p => new
                {
                    p.Pid, p.ImageName, p.Adapter, p.DedicatedGb, p.SharedGb, p.UtilizationPercent, p.Engine, p.CommandLine, p.IsSoftwareAdapter,
                    identity = p.Identity is null ? null : Describe(p.Identity),
                }),
            },
            identities,
        };
        File.WriteAllText(Path.Combine(dir, "probe.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        }));
        return 0;
    }

    /// <summary>What the cell and the tooltip show for a process; the command line is the redacted one.</summary>
    public static object Describe(ProcessIdentityInfo i) => new
    {
        pid = i.Key.Pid,
        image = i.ImageName,
        cell = i.CellName,
        detail = i.CellDetail,
        inferred = i.Title.Confidence != IdentityConfidence.Confirmed,
        titleBasis = i.Title.Basis,
        role = i.Role?.Text,
        executor = i.Executor?.Text,
        shared = i.IsSharedHost,
        hosted = i.Hosted.Select(h => h.Kind + ": " + h.Name),
        product = i.ProductName,
        company = i.CompanyName,
        icon = i.Icon is { } icon ? $"{icon.Width}x{icon.Height}" : null,
        commandLine = i.RedactedCommandLine,
        notes = i.Notes,
    };

    [LibraryImport("user32.dll")]
    internal static partial uint GetGuiResources(IntPtr process, uint flags);

    /// <summary>--soak-log: one CSV line (header first) with IRON's own resource use and the state of each source.</summary>
    public static void AppendSoakLine(string file, string sources)
    {
        var me = Process.GetCurrentProcess();
        var line = string.Join(",",
            DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
            (me.PrivateMemorySize64 / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            (me.WorkingSet64 / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            (GC.GetTotalMemory(false) / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            me.HandleCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            me.Threads.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GetGuiResources(me.Handle, 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            GetGuiResources(me.Handle, 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            me.TotalProcessorTime.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            "\"" + sources.Replace("\"", "'", StringComparison.Ordinal).Replace("\n", " | ", StringComparison.Ordinal) + "\"");
        if (!File.Exists(file))
        {
            File.AppendAllText(file, "time,privateMb,workingSetMb,managedMb,handles,threads,gdi,user,cpuSeconds,sources" + Environment.NewLine);
        }

        File.AppendAllText(file, line + Environment.NewLine);
    }
}
