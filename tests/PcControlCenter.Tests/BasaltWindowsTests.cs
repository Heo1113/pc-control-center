using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using PcControlCenter.Basalt;
using PcControlCenter.Basalt.Windows;
using PcControlCenter.Core;

namespace PcControlCenter.Tests;

/// <summary>
/// The Basalt control on real Windows processes (the CI runner): a temporary install folder whose start-basalt.ps1 runs
/// basalt-fake.exe (wrapper → server listening on a free port → engine), started in a console window of its own through
/// ShellExecute, stopped with Ctrl+C through PcControlCenter.ConsoleSignal.exe, and ended by the cleanup when it ignores
/// Ctrl+C. This is not Basalt itself: the real server, its Python process and RTX 5080 are checked on the user's PC only
/// (docs/VERIFICATION.md). Skipped elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
public class BasaltWindowsTests
{
    private const string WindowsOnly = "Windows processes, consoles and TCP table";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 180_000)]
    public async Task A_fake_basalt_tree_starts_in_its_own_window_and_stops_with_ctrl_c_leaving_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var install = new FakeInstall([]);
        await using var controller = install.Controller(TimeSpan.FromSeconds(15));
        try
        {
            Assert.Null(await controller.StartServerAsync(BasaltSettings.QualityMode, Ct));
            // The server listens before it starts its engine: wait for the whole tree, as a user would see it settle.
            var running = await WaitFor(controller, s => s.State == BasaltState.Running && s.Tree?.Members.Any(m => m.Role == TreeRole.Descendant) == true, 90);
            var tree = running.Tree!;
            Assert.True(tree.IsBasalt);
            Assert.Equal("basalt-fake.exe", tree.Owner?.Name, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(tree.Members, m => m.Role == TreeRole.Wrapper);
            Assert.Contains(tree.Members, m => m.Role == TreeRole.Descendant); // the engine
            Assert.Contains(tree.Members, m => m.Role == TreeRole.Launcher && m.Process.Name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(BasaltSettings.QualityMode, running.Mode);

            var report = await controller.StopServerAsync(Ct);

            Assert.NotNull(report);
            Assert.True(report.Graceful, $"{report.Signal}; ended {string.Join(", ", report.Ended)}; left {string.Join(", ", report.Left)}");
            Assert.True(report.Clean, report.Summary);
            Assert.Empty(report.Ended);
            Assert.Empty(StillRunning(tree));
            Assert.Equal(BasaltState.Off, controller.Status.State);
        }
        finally
        {
            install.KillLeftovers(controller.Status.Tree);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task A_tree_that_ignores_ctrl_c_is_ended_after_the_wait_listener_first_and_nothing_is_left()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var install = new FakeInstall(["--ignore-ctrl-c", "--respawn"]);
        await using var controller = install.Controller(TimeSpan.FromSeconds(3));
        BasaltTree? tree = null;
        try
        {
            Assert.Null(await controller.StartServerAsync(BasaltSettings.FastMode, Ct));
            tree = (await WaitFor(controller, s => s.State == BasaltState.Running && s.Tree?.Members.Any(m => m.Role == TreeRole.Descendant) == true, 90)).Tree!;

            var report = await controller.StopServerAsync(Ct);

            Assert.NotNull(report);
            Assert.False(report.Graceful);
            Assert.True(report.Clean, $"{report.Summary}; left {string.Join(", ", report.Left)}");
            Assert.StartsWith($"PID {tree.Owner!.Id} basalt-fake.exe (포트 소유)", report.Ended[0], StringComparison.OrdinalIgnoreCase);
            Assert.True(report.Took >= TimeSpan.FromSeconds(3));
            Assert.Empty(StillRunning(tree));
        }
        finally
        {
            install.KillLeftovers(tree ?? controller.Status.Tree);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Another_program_on_the_port_is_named_and_neither_started_over_nor_ended()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var install = new FakeInstall([], ((IPEndPoint)listener.LocalEndpoint).Port);
            await using var controller = install.Controller(TimeSpan.FromSeconds(5));

            var refusal = await controller.StartServerAsync(BasaltSettings.QualityMode, Ct);
            Assert.NotNull(refusal);
            Assert.Contains($"PID {Environment.ProcessId}", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(BasaltState.PortBusy, controller.Status.State);

            Assert.Null(await controller.StopServerAsync(Ct));
            Assert.Contains("Basalt로 확인되지 않아", controller.Status.Notice, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact(Timeout = 60_000)]
    public void A_process_is_ended_only_while_its_start_time_still_matches()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var engine = Process.Start(new ProcessStartInfo(FakeInstall.FakeExe, "engine") { UseShellExecute = false, CreateNoWindow = true })!;
        var system = new WindowsBasaltSystem(FakeInstall.SignalHelper);
        try
        {
            var listed = system.Processes()!.Single(p => p.Id == engine.Id);
            Assert.NotNull(listed.StartTime);

            Assert.Equal(TerminateResult.IdentityChanged, system.Terminate(listed with { StartTime = listed.StartTime!.Value.AddSeconds(-5) }));
            Assert.Equal(TerminateResult.Unverified, system.Terminate(listed with { StartTime = null }));
            Assert.False(engine.HasExited);

            Assert.Equal(TerminateResult.Ended, system.Terminate(listed));
            Assert.True(engine.WaitForExit(5000));
            Assert.Equal(TerminateResult.AlreadyGone, system.Terminate(listed));
        }
        finally
        {
            if (!engine.HasExited)
            {
                engine.Kill();
            }

            system.Dispose();
        }
    }

    private static List<int> StillRunning(BasaltTree tree)
    {
        using var system = new WindowsBasaltSystem(null);
        var now = system.Processes()!;
        return [.. BasaltProcessTree.StillRunning(tree.Members.Select(m => m.Process), now).Select(p => p.Id)];
    }

    private static async Task<BasaltStatus> WaitFor(BasaltController controller, Func<BasaltStatus, bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            await controller.RefreshAsync(Ct);
            var status = controller.Status;
            if (condition(status))
            {
                return status;
            }

            Assert.True(DateTime.UtcNow < deadline, $"state {status.State}: {status.Detail} (notice: {status.Notice})");
            await Task.Delay(250, Ct);
        }
    }

    /// <summary>The runner runs elevated; the elevation refusal is tested with the fake system, the rest here for real.</summary>
    private sealed class NotElevated(WindowsBasaltSystem inner) : IBasaltSystem, IDisposable
    {
        public string Name => inner.Name;
        public bool IsMock => false;
        public bool IsElevated => false;
        public IReadOnlyList<BasaltProcess>? Processes() => inner.Processes();
        public IReadOnlyList<BasaltListener>? Listeners(int port) => inner.Listeners(port);
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public BasaltLaunch Start(BasaltStartRequest request, string mode) => inner.Start(request, mode);
        public ConsoleSignalResult SendCtrlC(int processId) => inner.SendCtrlC(processId);
        public TerminateResult Terminate(BasaltProcess process) => inner.Terminate(process);
        public Task<BasaltHealth> HealthAsync(int port, CancellationToken cancellationToken) => inner.HealthAsync(port, cancellationToken);
        public void Dispose() => inner.Dispose();
    }

    /// <summary>A temporary "C:\AI\basalt": start-basalt.ps1 and a config that runs basalt-fake.exe on a free port.</summary>
    private sealed class FakeInstall : IDisposable
    {
        private static readonly string Configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        public static readonly string FakeExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PcControlCenter.FakeBasalt", "bin",
            Configuration, "net10.0", "basalt-fake.exe"));

        public static readonly string SignalHelper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src",
            "PcControlCenter.ConsoleSignal", "bin", Configuration, "net10.0", "PcControlCenter.ConsoleSignal.exe"));

        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pcc-basalt-{Guid.NewGuid():N}");

        public FakeInstall(string[] flags, int? port = null)
        {
            Assert.True(File.Exists(FakeExe), FakeExe);
            Assert.True(File.Exists(SignalHelper), SignalHelper);
            Directory.CreateDirectory(_directory);
            Port = port ?? FreePort();
            File.WriteAllText(Path.Combine(_directory, "start-basalt.ps1"), """
                param([string]$Config)
                $cfg = Get-Content -Raw -LiteralPath $Config | ConvertFrom-Json
                $flags = @($cfg.flags)
                & $cfg.exe wrapper --port $cfg.port @flags
                """);
            var config = JsonSerializer.Serialize(new { exe = FakeExe, port = Port, flags });
            File.WriteAllText(Path.Combine(_directory, "basalt_config.json"), config);
            File.WriteAllText(Path.Combine(_directory, "basalt_config.fast.json"), config);
        }

        public int Port { get; }

        public BasaltController Controller(TimeSpan stopWait)
            => new(new NotElevated(new WindowsBasaltSystem(SignalHelper)), new BasaltSettings { InstallDirectory = _directory, Port = Port },
                timings: new BasaltTimings
                {
                    Poll = TimeSpan.FromMilliseconds(250),
                    FastPoll = TimeSpan.FromMilliseconds(100),
                    StopWait = stopWait,
                    StartWait = TimeSpan.FromSeconds(90),
                });

        /// <summary>Test cleanup only: nothing of a failed test may stay on the runner.</summary>
        public void KillLeftovers(BasaltTree? tree)
        {
            foreach (var process in Process.GetProcessesByName("basalt-fake"))
            {
                using (process)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }

            foreach (var member in tree?.Members.Where(m => m.Role == TreeRole.Launcher) ?? [])
            {
                try
                {
                    using var launcher = Process.GetProcessById(member.Process.Id);
                    if (launcher.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                    {
                        launcher.Kill();
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
