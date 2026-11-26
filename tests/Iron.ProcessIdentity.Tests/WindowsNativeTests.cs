using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Iron.ProcessIdentity;

namespace Iron.ProcessIdentity.Tests;

/// <summary>The native layer on a real Windows machine (CI runner). Skipped on other systems.</summary>
[SupportedOSPlatform("windows")]
public class WindowsNativeTests
{
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static void WindowsOnly() => Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows 전용");

    [Fact]
    public void SnapshotHasCreationTimesAndOwnProcess()
    {
        WindowsOnly();
        using var q = new WindowsProcessQuery();
        var list = q.Snapshot();
        Assert.True(list.Count > 20);
        var me = Assert.Single(list, p => p.Pid == Environment.ProcessId);
        Assert.True(me.CreateTimeTicks > 0);
        Assert.Contains(list, p => p.ImageName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OwnImageCommandLineAndCmdVersionInfoWithIcon()
    {
        WindowsOnly();
        using var q = new WindowsProcessQuery();
        Assert.NotNull(q.QueryImagePath(Environment.ProcessId, out _));
        Assert.False(string.IsNullOrEmpty(q.QueryCommandLine(Environment.ProcessId, out _)));
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var f = q.ReadFile(cmd);
        Assert.NotNull(f);
        Assert.False(string.IsNullOrEmpty(f!.FileDescription));
        Assert.True(f.CompanyIsMicrosoft);
        Assert.NotNull(f.Icon);
        Assert.True(f.Icon!.Width >= 16 && f.Icon.Bgra.Length == f.Icon.Width * f.Icon.Height * 4);
    }

    [Fact]
    public void ServicesMapSvchostPidsWithoutOpeningProcesses()
    {
        WindowsOnly();
        using var q = new WindowsProcessQuery();
        var services = q.QueryServices();
        Assert.NotNull(services);
        var svchostPids = q.Snapshot().Where(p => p.ImageName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)).Select(p => p.Pid).ToHashSet();
        Assert.Contains(services!.Keys, svchostPids.Contains);
        Assert.DoesNotContain(services.Values.SelectMany(v => v), s => s.DisplayName.StartsWith('@')); // indirect strings expanded
    }

    [Fact]
    public void IconExtractionDoesNotLeakGdiHandles()
    {
        WindowsOnly();
        using var q = new WindowsProcessQuery();
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        q.ReadFile(cmd);
        var before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        for (var i = 0; i < 200; i++)
        {
            q.ReadFile(cmd);
        }

        var after = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        Assert.True(after <= before + 4, $"GDI objects {before} → {after}");
    }

    [Fact]
    public void ResolverIdentifiesARealCmdChild()
    {
        WindowsOnly();
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 20 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            using var r = IdentityResolverFactory.TryCreateNative()!;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            ProcessIdentityInfo? info = null;
            while (DateTime.UtcNow < deadline && info is null)
            {
                var snap = new WindowsProcessQuery().Snapshot();
                r.Observe(snap);
                var e = snap.FirstOrDefault(p => p.Pid == child.Id);
                if (e is not null && r.TryGet(e.Key, out var got))
                {
                    info = got;
                }

                Thread.Sleep(200);
            }

            Assert.NotNull(info);
            Assert.Equal("명령 ping", info!.Executor!.Text.Split(" · ")[0]);
            Assert.Equal(IdentitySource.Native, info.Source);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
    }
}
