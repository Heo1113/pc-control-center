using PcControlCenter.Basalt;
using PcControlCenter.Basalt.Mock;
using PcControlCenter.Core;
using static PcControlCenter.Tests.FakeBasaltSystem;

namespace PcControlCenter.Tests;

/// <summary>Which processes are Basalt, and the order they would be ended in (pure logic, no OS).</summary>
public class BasaltProcessTreeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private static (IReadOnlyList<BasaltProcess> Processes, IReadOnlyList<BasaltListener> Listeners) Table(FakeBasaltSystem system)
        => (system.Processes()!, system.Listeners(8080)!);

    [Fact]
    public void The_listener_its_wrapper_and_its_engine_are_basalt_and_the_listener_is_ended_first()
    {
        var system = new FakeBasaltSystem(T0);
        system.AddTree(T0);
        var (processes, listeners) = Table(system);

        var tree = BasaltProcessTree.Identify(8080, processes, listeners, launch: null, Self);

        Assert.True(tree.IsBasalt);
        Assert.Equal(Server, tree.Owner?.Id);
        // Listener first (it would start the engine again), then the wrapper, then the engine; no console host, no window
        // (not started by this app), never explorer or this app.
        Assert.Equal([Server, Wrapper, Engine], tree.Members.Select(m => m.Process.Id));
        Assert.Equal([TreeRole.Listener, TreeRole.Wrapper, TreeRole.Descendant], tree.Members.Select(m => m.Role));
        Assert.Equal(Server, tree.ConsoleCandidates[0].Id);
    }

    [Fact]
    public void The_window_this_app_started_is_ended_last()
    {
        var system = new FakeBasaltSystem(T0);
        system.AddTree(T0);
        var (processes, listeners) = Table(system);

        var tree = BasaltProcessTree.Identify(8080, processes, listeners, new BasaltLaunch(Launcher, T0, BasaltSettings.QualityMode, T0), Self);

        Assert.Equal([Server, Wrapper, Engine, Launcher], tree.Members.Select(m => m.Process.Id));
        Assert.DoesNotContain(tree.Members, m => m.Process.Id == ConsoleHost);
    }

    [Fact]
    public void An_engine_started_by_the_wrapper_beside_the_listener_belongs_to_it()
    {
        var system = new FakeBasaltSystem(T0);
        system.AddTree(T0);
        system.Add(new BasaltProcess(Engine, Wrapper, "basalt-engine.exe", T0.AddSeconds(1)));
        var (processes, listeners) = Table(system);

        var tree = BasaltProcessTree.Identify(8080, processes, listeners, null, Self);

        Assert.Contains(tree.Members, m => m.Process.Id == Engine && m.Role == TreeRole.WrapperDescendant);
    }

    [Fact]
    public void A_process_older_than_its_parent_is_not_its_child_and_an_unknown_start_time_is_not_verified()
    {
        var system = new FakeBasaltSystem(T0);
        system.AddTree(T0);
        system.Add(new BasaltProcess(540, Server, "old-tool.exe", T0.AddMinutes(-10))); // the server's PID was reused after it started
        system.Add(new BasaltProcess(541, Server, "helper.exe", null));
        var (processes, listeners) = Table(system);

        var tree = BasaltProcessTree.Identify(8080, processes, listeners, null, Self);

        Assert.DoesNotContain(tree.Members, m => m.Process.Id is 540 or 541);
        Assert.Contains(tree.Unverified, u => u.Contains("PID 541", StringComparison.Ordinal));
    }

    [Fact]
    public void Another_program_on_the_port_is_not_basalt_and_nothing_of_it_would_be_ended()
    {
        var system = new FakeBasaltSystem(T0);
        system.Add(new BasaltProcess(700, Explorer, "nginx.exe", T0));
        system.Add(new BasaltProcess(701, 700, "nginx.exe", T0.AddSeconds(1)));
        system.Listen(700);
        var (processes, listeners) = Table(system);

        var tree = BasaltProcessTree.Identify(8080, processes, listeners, null, Self);

        Assert.False(tree.IsBasalt);
        Assert.Empty(tree.Members);
        Assert.Empty(tree.ConsoleCandidates);
        Assert.Contains("nginx.exe", tree.NotBasaltReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(Self)]
    public void The_kernel_or_this_app_on_the_port_is_never_basalt(int owner)
    {
        var system = new FakeBasaltSystem(T0);
        system.Add(new BasaltProcess(4, 0, "System", null));
        system.Listen(owner);
        var (processes, listeners) = Table(system);

        var tree = BasaltProcessTree.Identify(8080, processes, listeners, null, Self);

        Assert.False(tree.IsBasalt);
        Assert.Empty(tree.Members);
    }

    [Fact]
    public void A_listener_under_the_window_this_app_started_is_basalt_whatever_its_name()
    {
        var system = new FakeBasaltSystem(T0);
        system.Add(new BasaltProcess(Launcher, Explorer, "powershell.exe", T0));
        system.Add(new BasaltProcess(Server, Launcher, "python.exe", T0.AddSeconds(1)));
        system.Listen(Server);
        var (processes, listeners) = Table(system);

        Assert.False(BasaltProcessTree.Identify(8080, processes, listeners, null, Self).IsBasalt);
        var ours = BasaltProcessTree.Identify(8080, processes, listeners, new BasaltLaunch(Launcher, T0, BasaltSettings.QualityMode, T0), Self);
        Assert.True(ours.IsBasalt);
        Assert.Equal([Server, Launcher], ours.Members.Select(m => m.Process.Id));
    }

    [Fact]
    public void With_nothing_listening_basalt_processes_left_behind_are_found_by_name()
    {
        var system = new FakeBasaltSystem(T0);
        system.AddTree(T0, listening: false, withLauncher: false);
        system.Remove(Server); // the engine and the wrapper are left
        var (processes, listeners) = Table(system);

        var tree = BasaltProcessTree.Identify(8080, processes, listeners, null, Self);

        Assert.True(tree.IsBasalt);
        Assert.Null(tree.Owner);
        Assert.Equal([Wrapper, Engine], tree.Members.Select(m => m.Process.Id).Order());
    }

    [Fact]
    public void New_children_of_ended_processes_are_found_and_reused_pids_are_not()
    {
        var parent = new BasaltProcess(Server, Wrapper, "python.exe", T0);
        var now = new List<BasaltProcess>
        {
            new(Wrapper, Explorer, "basalt.exe", T0.AddSeconds(-1)),
            new(531, Server, "basalt-engine.exe", T0.AddSeconds(5)),
            new(532, 531, "helper.exe", T0.AddSeconds(6)),
            new(533, Server, "unrelated.exe", T0.AddSeconds(-5)),
        };

        var born = BasaltProcessTree.NewChildren([parent], now, new HashSet<int> { Server }, Self);

        Assert.Equal([531, 532], born.Select(p => p.Id));
    }
}

/// <summary>Starting and stopping Basalt against a fake system (states, refusals, Ctrl+C, the order processes are ended in).</summary>
public class BasaltControllerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30_000)]
    public async Task Start_runs_the_start_script_with_the_quality_config_and_follows_it_until_it_answers()
    {
        var system = new FakeBasaltSystem();
        await using var controller = BasaltTest.Controller(system);

        Assert.Null(await controller.StartServerAsync(BasaltSettings.QualityMode, Ct));

        var request = Assert.Single(system.Starts);
        Assert.EndsWith("powershell.exe", request.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"-NoProfile -ExecutionPolicy Bypass -File ""C:\AI\basalt\start-basalt.ps1"" -Config ""C:\AI\basalt\basalt_config.json""", request.Arguments);
        Assert.Equal(@"C:\AI\basalt", request.WorkingDirectory);
        var status = await BasaltTest.WaitFor(controller, s => s.State == BasaltState.Running);
        Assert.True(status.StartedHere);
        Assert.Equal(BasaltSettings.QualityMode, status.Mode);
        Assert.Null(status.Notice);
    }

    [Fact(Timeout = 30_000)]
    public async Task The_fast_mode_uses_the_fast_config_from_the_settings()
    {
        var system = new FakeBasaltSystem();
        var settings = new BasaltSettings { InstallDirectory = @"D:\Tools\basalt", FastConfig = @"E:\cfg\fast.json" };
        await using var controller = BasaltTest.Controller(system, settings);

        Assert.Null(await controller.StartServerAsync(BasaltSettings.FastMode, Ct));

        Assert.Equal(@"-NoProfile -ExecutionPolicy Bypass -File ""D:\Tools\basalt\start-basalt.ps1"" -Config ""E:\cfg\fast.json""", system.Starts[0].Arguments);
        Assert.Equal(BasaltSettings.FastMode, (await BasaltTest.WaitFor(controller, s => s.State == BasaltState.Running)).Mode);
    }

    [Fact(Timeout = 30_000)]
    public async Task Another_program_on_the_port_keeps_basalt_from_starting_and_is_named()
    {
        var system = new FakeBasaltSystem();
        system.Add(new BasaltProcess(700, Explorer, "nginx.exe", DateTimeOffset.UtcNow.AddMinutes(-1)));
        system.Listen(700);
        await using var controller = BasaltTest.Controller(system);

        var refusal = await controller.StartServerAsync(BasaltSettings.QualityMode, Ct);

        Assert.NotNull(refusal);
        Assert.Contains("PID 700 nginx.exe", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(system.Starts);
        Assert.Equal(BasaltState.PortBusy, controller.Status.State);
        Assert.Equal(refusal.Message, controller.Status.Notice);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_running_basalt_is_recognised_and_not_started_twice()
    {
        var system = new FakeBasaltSystem();
        system.AddTree(DateTimeOffset.UtcNow.AddMinutes(-2), withLauncher: false);
        await using var controller = BasaltTest.Controller(system);

        var refusal = await controller.StartServerAsync(BasaltSettings.FastMode, Ct);

        Assert.Contains("이미", refusal?.Message, StringComparison.Ordinal);
        Assert.Empty(system.Starts);
        var status = controller.Status;
        Assert.Equal(BasaltState.Running, status.State);
        Assert.False(status.StartedHere);
        Assert.Null(status.Mode); // started elsewhere: the mode is not known and not guessed
    }

    [Fact(Timeout = 30_000)]
    public async Task Basalt_is_not_started_elevated_without_its_files_or_without_the_tcp_table()
    {
        var elevated = new FakeBasaltSystem { IsElevated = true };
        await using (var controller = BasaltTest.Controller(elevated))
        {
            Assert.Contains("관리자 권한", (await controller.StartServerAsync(BasaltSettings.QualityMode, Ct))?.Message, StringComparison.Ordinal);
        }

        var missing = new FakeBasaltSystem { MissingFiles = true };
        await using (var controller = BasaltTest.Controller(missing))
        {
            Assert.Contains(@"C:\AI\basalt\start-basalt.ps1", (await controller.StartServerAsync(BasaltSettings.QualityMode, Ct))?.Message, StringComparison.Ordinal);
        }

        var blind = new FakeBasaltSystem { ListenersUnreadable = true };
        await using (var controller = BasaltTest.Controller(blind))
        {
            Assert.Contains("TCP", (await controller.StartServerAsync(BasaltSettings.QualityMode, Ct))?.Message, StringComparison.Ordinal);
        }

        Assert.Empty(elevated.Starts);
        Assert.Empty(missing.Starts);
        Assert.Empty(blind.Starts);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_start_whose_window_closes_without_basalt_is_reported()
    {
        var system = new FakeBasaltSystem { StartCreatesTree = false };
        await using var controller = BasaltTest.Controller(system);
        Assert.Null(await controller.StartServerAsync(BasaltSettings.QualityMode, Ct));
        Assert.Equal(BasaltState.Starting, controller.Status.State);

        system.Remove(Launcher);
        var status = await BasaltTest.WaitFor(controller, s => s.State == BasaltState.Off && s.Notice is not null);

        Assert.Contains("켜지 못했습니다", status.Notice, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task Stop_sends_ctrl_c_to_the_listeners_console_and_reports_nothing_left()
    {
        var system = new FakeBasaltSystem();
        await using var controller = BasaltTest.Controller(system);
        await controller.StartServerAsync(BasaltSettings.QualityMode, Ct);
        await BasaltTest.WaitFor(controller, s => s.State == BasaltState.Running);

        var report = await controller.StopServerAsync(Ct);

        Assert.NotNull(report);
        Assert.True(report.Graceful);
        Assert.True(report.Clean);
        Assert.Equal([Server], system.Signals);
        Assert.Empty(system.Terminated);
        Assert.Contains("PID 520 python.exe", report.Signal, StringComparison.Ordinal);
        var status = controller.Status;
        Assert.Equal(BasaltState.Off, status.State);
        Assert.Same(report, status.LastStop);
        Assert.Contains("남은 프로세스 없음", report.Summary, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task After_the_wait_the_listener_is_ended_first_then_the_wrapper_the_engine_and_the_window()
    {
        var system = new FakeBasaltSystem { Signal = FakeSignal.Ignored };
        await using var controller = BasaltTest.Controller(system);
        await controller.StartServerAsync(BasaltSettings.QualityMode, Ct);
        await BasaltTest.WaitFor(controller, s => s.State == BasaltState.Running);

        var report = await controller.StopServerAsync(Ct);

        Assert.NotNull(report);
        Assert.False(report.Graceful);
        Assert.Equal([Server, Wrapper, Engine, Launcher], system.Terminated);
        Assert.True(report.Clean);
        Assert.True(report.Took >= TimeSpan.FromMilliseconds(300)); // the graceful wait came first
        Assert.Contains(report.Ended, e => e.StartsWith("PID 520 python.exe (포트 소유)", StringComparison.Ordinal));
        Assert.True(system.Has(Explorer));
        Assert.True(system.Has(Self));
    }

    [Fact(Timeout = 30_000)]
    public async Task Without_a_console_the_processes_are_ended_without_waiting()
    {
        var system = new FakeBasaltSystem { Signal = FakeSignal.NoConsole };
        system.AddTree(DateTimeOffset.UtcNow.AddMinutes(-1), withLauncher: false);
        await using var controller = BasaltTest.Controller(system, timings: BasaltTest.Fast with { StopWait = TimeSpan.FromSeconds(20) });

        var report = await controller.StopServerAsync(Ct);

        Assert.NotNull(report);
        Assert.Contains("콘솔이 없음", report.Signal, StringComparison.Ordinal);
        Assert.Equal([Server, Wrapper, Engine], system.Signals); // tried in order
        Assert.Equal([Server, Wrapper, Engine], system.Terminated);
        Assert.True(report.Took < TimeSpan.FromSeconds(10));
        Assert.True(report.Clean);
    }

    [Fact(Timeout = 30_000)]
    public async Task An_engine_started_again_while_ending_is_ended_too()
    {
        var system = new FakeBasaltSystem { Signal = FakeSignal.Ignored };
        system.AfterTerminate = (s, ended) =>
        {
            if (ended.Id == Wrapper)
            {
                // The wrapper started a new engine just before it went.
                s.Add(new BasaltProcess(532, Wrapper, "basalt-engine.exe", DateTimeOffset.UtcNow));
            }
        };
        system.AddTree(DateTimeOffset.UtcNow.AddMinutes(-1), withLauncher: false);
        await using var controller = BasaltTest.Controller(system);

        var report = await controller.StopServerAsync(Ct);

        Assert.NotNull(report);
        Assert.Contains(532, system.Terminated);
        Assert.False(system.Has(532));
        Assert.Contains(report.Ended, e => e.Contains("PID 532", StringComparison.Ordinal) && e.Contains("다시 생긴", StringComparison.Ordinal));
        Assert.True(report.Clean);
    }

    [Fact(Timeout = 30_000)]
    public async Task Nothing_is_stopped_when_the_port_belongs_to_another_program()
    {
        var system = new FakeBasaltSystem();
        system.Add(new BasaltProcess(700, Explorer, "nginx.exe", DateTimeOffset.UtcNow.AddMinutes(-1)));
        system.Listen(700);
        system.Add(new BasaltProcess(Engine, Explorer, "basalt-engine.exe", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await using var controller = BasaltTest.Controller(system);

        var report = await controller.StopServerAsync(Ct);

        Assert.Null(report);
        Assert.Empty(system.Signals);
        Assert.Empty(system.Terminated);
        Assert.Contains("Basalt로 확인되지 않아", controller.Status.Notice, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_refused_process_is_reported_as_left_and_a_reused_pid_is_left_alone()
    {
        var system = new FakeBasaltSystem { Signal = FakeSignal.Ignored };
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        system.AddTree(start, withLauncher: false);
        system.TerminateOverride = p => p.Id switch
        {
            Wrapper => TerminateResult.Denied,
            _ => null,
        };
        system.AfterTerminate = (s, ended) =>
        {
            if (ended.Id == Server)
            {
                // The engine's PID now belongs to a newer process when its turn comes.
                s.Add(new BasaltProcess(Engine, Explorer, "notepad.exe", DateTimeOffset.UtcNow));
            }
        };
        await using var controller = BasaltTest.Controller(system);

        var report = await controller.StopServerAsync(Ct);

        Assert.NotNull(report);
        Assert.False(report.Clean);
        var left = Assert.Single(report.Left);
        Assert.StartsWith("PID 510 basalt.exe — Windows가 거부함", left, StringComparison.Ordinal);
        Assert.True(system.Has(Engine)); // notepad, not the engine: not ended
        Assert.Equal(BasaltState.Leftover, controller.Status.State);
    }

    [Fact(Timeout = 30_000)]
    public async Task Stopping_what_is_off_does_nothing()
    {
        var system = new FakeBasaltSystem();
        await using var controller = BasaltTest.Controller(system);

        Assert.Null(await controller.StopServerAsync(Ct));

        Assert.Contains("이미 꺼져", controller.Status.Notice, StringComparison.Ordinal);
        Assert.Empty(system.Signals);
        Assert.Equal(BasaltState.Off, controller.Status.State);
    }

    [Fact(Timeout = 30_000)]
    public async Task The_mock_server_starts_answers_and_stops_with_ctrl_c()
    {
        using var mock = new MockBasaltSystem { StartDelay = TimeSpan.FromMilliseconds(100) };
        await using var controller = new BasaltController(mock, new BasaltSettings(), timings: BasaltTest.Fast with { StopWait = TimeSpan.FromSeconds(5) });

        Assert.Equal(mock.PortOverride, controller.Port);
        Assert.Null(await controller.StartServerAsync(BasaltSettings.QualityMode, Ct));
        var running = await BasaltTest.WaitFor(controller, s => s.State == BasaltState.Running);
        Assert.True(running.IsMock);
        using var http = new HttpClient();
        var page = await http.GetStringAsync($"http://127.0.0.1:{controller.Port}/", Ct);
        Assert.Contains("MOCK", page, StringComparison.Ordinal);

        var report = await controller.StopServerAsync(Ct);

        Assert.NotNull(report);
        Assert.True(report.Graceful);
        Assert.True(report.Clean);
    }
}

public class BasaltSettingsTests
{
    [Fact]
    public void Defaults_point_at_the_install_folder_and_relative_paths_are_taken_from_it()
    {
        var settings = new BasaltSettings();

        Assert.Empty(settings.Normalize());
        Assert.Equal(@"C:\AI\basalt\start-basalt.ps1", settings.Resolve(settings.StartScript));
        Assert.Equal(@"C:\AI\basalt\basalt_config.json", settings.Resolve(settings.ConfigFor(BasaltSettings.QualityMode)));
        Assert.Equal(@"C:\AI\basalt\basalt_config.fast.json", settings.Resolve(settings.ConfigFor(BasaltSettings.FastMode)));
        Assert.Equal(@"E:\x\y.json", settings.Resolve(@"E:\x\y.json"));
        Assert.Equal(@"\\nas\ai\b.json", settings.Resolve(@"\\nas\ai\b.json"));
        Assert.Equal(8080, settings.Port);
        Assert.Equal(30, settings.StopWaitSeconds);
    }

    [Fact]
    public void Out_of_range_values_are_replaced_with_a_note_each()
    {
        var settings = new BasaltSettings
        {
            InstallDirectory = "",
            StartScript = "start.bat",
            QualityConfig = "a\"b.json",
            FastConfig = "c*.json",
            Port = 0,
            StopWaitSeconds = 1,
            StartWaitSeconds = 99999,
            Mode = "Turbo",
        };

        var notes = settings.Normalize();

        Assert.Equal(7, notes.Count);
        Assert.Equal(BasaltSettings.DefaultInstallDirectory, settings.InstallDirectory);
        Assert.Equal(BasaltSettings.DefaultStartScript, settings.StartScript);
        Assert.Equal(BasaltSettings.DefaultQualityConfig, settings.QualityConfig);
        Assert.Equal(BasaltSettings.DefaultFastConfig, settings.FastConfig);
        Assert.Equal(8080, settings.Port);
        Assert.Equal(30, settings.StopWaitSeconds);
        Assert.Equal(900, settings.StartWaitSeconds);
        Assert.Equal(BasaltSettings.QualityMode, settings.Mode);
    }

    [Fact]
    public void Older_settings_files_get_the_basalt_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcc-basalt-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "SchemaVersion": 1, "Ui": { "FontScale": 1.0 } }""");
            var loaded = new ShellSettingsStore(path).Load();
            Assert.Equal(BasaltSettings.DefaultInstallDirectory, loaded.Settings.Basalt.InstallDirectory);
            Assert.Equal(8080, loaded.Settings.Basalt.Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(@"C:\AI\basalt\start-basalt.ps1", @"""C:\AI\basalt\start-basalt.ps1""")]
    [InlineData(@"C:\Program Files\Basalt\s.ps1", @"""C:\Program Files\Basalt\s.ps1""")]
    [InlineData(@"C:\dir\", @"""C:\dir\\""")]
    public void Arguments_are_quoted_so_windows_splits_them_back(string path, string quoted)
        => Assert.Equal(quoted, BasaltCommand.Quote(path));
}

public class BasaltHealthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30_000)]
    public async Task Health_reads_the_status_service_and_loaded_and_nothing_else()
    {
        var answer = new LoopbackResponse(200, "application/json", """{"service":"basalt","loaded":false,"api_key":"must-not-be-read"}""");
        using var server = new LoopbackHttpServer(path => path == "/health" ? answer : new LoopbackResponse(404, "text/plain", ""));
        using var client = new BasaltHealthClient();

        var health = await client.GetAsync(server.Port, Ct);

        Assert.Equal(new BasaltHealth(200, "basalt", false, null), health);
        Assert.True(health.IsUp);

        answer = new LoopbackResponse(503, "application/json", "{}");
        Assert.False((await client.GetAsync(server.Port, Ct)).IsUp);
    }

    [Fact(Timeout = 30_000)]
    public async Task Nothing_listening_is_no_answer()
    {
        int port;
        using (var server = new LoopbackHttpServer(_ => new LoopbackResponse(200, "text/plain", "")))
        {
            port = server.Port;
        }

        using var client = new BasaltHealthClient();
        var health = await client.GetAsync(port, Ct);

        Assert.Null(health.StatusCode);
        Assert.False(health.IsUp);
        Assert.NotNull(health.Error);
    }
}
