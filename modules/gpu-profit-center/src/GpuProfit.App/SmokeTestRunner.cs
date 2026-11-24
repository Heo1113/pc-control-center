using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GpuProfit.App.Services;
using GpuProfit.Core.Domain;
using GpuProfit.Presentation;
using GpuProfit.Presentation.ViewModels;
using GpuProfit.Recording;
using GpuProfit.Storage;

namespace GpuProfit.App;

/// <summary>
/// --smoke-test: drives every screen of the real window with this machine's sensors (or --mock-sensors), uses the main
/// commands (work item, recording with pause/resume, revenue, cost, comparison, scenarios, history export, accounting
/// conditions, backup, JSON export), saves a screenshot per screen and a JSON report, then exits with 0 (pass) or 1.
/// It checks that the GUI starts, binds and works; it does not check measurement accuracy.
/// Structure adapted from project C's SmokeTestRunner (commit 06cc9e5).
/// </summary>
internal sealed class SmokeTestRunner(Window window, MainViewModel main, ProfitEngine engine, WpfDialogService dialogs, string reportDirectory)
{
    private readonly List<string> _exceptions = [];
    private readonly List<string> _screens = [];
    private readonly BindingErrorListener _bindings = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, bool> _checks = [];
    private readonly Dictionary<string, string> _details = [];

    public int? ExitCode { get; private set; }

    public void Start()
    {
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(_bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        _ = RunAsync();
    }

    public void RecordException(Exception ex)
    {
        lock (_exceptions)
        {
            _exceptions.Add($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task Check(string name, Func<Task<bool>> step)
    {
        try
        {
            _checks[name] = await step();
        }
        catch (Exception ex)
        {
            RecordException(ex);
            _checks[name] = false;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await RunStepsAsync();
        }
        catch (Exception ex)
        {
            RecordException(ex);
        }
        finally
        {
            WriteReport();
            window.Close();
        }
    }

    private async Task RunStepsAsync()
    {
        await Check("sensor-sample", () => WaitUntil(() => engine.Recorder?.Live.Latest is not null, TimeSpan.FromSeconds(30)));

        foreach (var page in main.Pages)
        {
            main.CurrentPage = page;
            await Task.Delay(1200);
            await WaitUntil(() => !page.IsBusy, TimeSpan.FromSeconds(60));
            await Capture(page.GetType().Name.Replace("ViewModel", "", StringComparison.Ordinal));
        }

        long sessionId = 0;
        await Check("session-record-pause-resume-stop", async () =>
        {
            var s = main.Sessions;
            main.CurrentPage = s;
            await s.RefreshAsync();
            s.NewWorkloadName = $"스모크 테스트 작업 {DateTime.Now:HHmmss}";
            s.NewWorkloadType = s.Types.First(t => t.Value == WorkloadType.GpuCompute);
            await s.AddWorkloadCommand.ExecuteAsync(null);
            s.StartTitle = "스모크 테스트 세션";
            await s.StartCommand.ExecuteAsync(null);
            await Task.Delay(TimeSpan.FromSeconds(6));
            await s.PauseCommand.ExecuteAsync(null);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await s.ResumeCommand.ExecuteAsync(null);
            await Task.Delay(TimeSpan.FromSeconds(5));
            await s.StopCommand.ExecuteAsync(null);
            await WaitUntil(() => !s.IsBusy, TimeSpan.FromSeconds(30));
            await Capture("Sessions-after-stop");
            sessionId = s.SelectedSession?.Id ?? 0;
            var stored = engine.Store.GetSession(sessionId);
            _details["sessionStatus"] = s.StatusText;
            return stored is { State: SessionState.Ended } && engine.Store.ListSegments(sessionId).Count == 2 && engine.Store.ListResults(sessionId).Count > 0;
        });

        await Check("profitability-revenue-cost-history", async () =>
        {
            var p = main.Profitability;
            main.ShowProfitability(sessionId);
            await Task.Delay(1000);
            await WaitUntil(() => !p.IsBusy, TimeSpan.FromSeconds(30));
            p.RevAmount = "1000";
            p.RevCurrency = "KRW";
            p.RevOccurred = Fmt.InputTime(DateTimeOffset.Now);
            await p.AddRevenueCommand.ExecuteAsync(null);
            p.SelectedCostBasis = p.CostBases.First(b => b.Value == CostBasis.PercentOfRevenue);
            p.CostScope = p.CostScopes[0];
            p.CostAmount = "1";
            await p.AddCostCommand.ExecuteAsync(null);
            await p.SaveResultCommand.ExecuteAsync(null);
            await Capture("Profitability-session");
            _details["profitabilityStatus"] = p.StatusText;
            return p.Revenue.Count > 0 && p.History.Count > 0 && p.Headline.Count > 0 && engine.Store.ListRevenue(sessionId).Count == 1;
        });

        await Check("compare", async () =>
        {
            var c = main.Compare;
            main.CurrentPage = c;
            await c.LoadCandidatesAsync();
            c.SelectAllCommand.Execute(null);
            await c.CompareCommand.ExecuteAsync(null);
            await c.ExportCommand.ExecuteAsync(null);
            await Capture("Compare-result");
            return c.Rows.Count > 0 && Directory.GetFiles(reportDirectory, "gpc-compare-*.csv").Length > 0;
        });

        await Check("scenarios-sensitivity", async () =>
        {
            var sc = main.Scenarios;
            main.CurrentPage = sc;
            await Task.Delay(800);
            await WaitUntil(() => !sc.IsBusy, TimeSpan.FromSeconds(30));
            sc.SourceSession = sc.SessionOptions.FirstOrDefault(o => o.Value == sessionId) ?? sc.SessionOptions.FirstOrDefault();
            await sc.FromSessionCommand.ExecuteAsync(null);
            sc.Name = "스모크 테스트 시나리오";
            await sc.SaveCommand.ExecuteAsync(null);
            await sc.VariantsCommand.ExecuteAsync(null);
            await sc.CalculateCommand.ExecuteAsync(null);
            await Capture("Scenarios-result");
            return sc.HasResult && sc.SensitivityChart is not null && sc.Comparison.Count >= 3 && sc.Tornado.Count > 0;
        });

        await Check("history-export", async () =>
        {
            var h = main.History;
            main.CurrentPage = h;
            await Task.Delay(1500);
            await WaitUntil(() => !h.IsBusy, TimeSpan.FromSeconds(60));
            await h.ExportCommand.ExecuteAsync(null);
            await Capture("History-periods");
            return h.Periods.Count > 0 && Directory.GetFiles(reportDirectory, "gpc-periods-*.csv").Length > 0;
        });

        await Check("settings-profile-backup-export", async () =>
        {
            var st = main.Settings;
            main.CurrentPage = st;
            await Task.Delay(800);
            st.ExtraW = "60";
            await st.SaveProfileCommand.ExecuteAsync(null);
            var profileSaved = engine.Profile.ExtraSystemPowerW == 60;
            await st.BackupCommand.ExecuteAsync(null);
            await st.ExportLedgerCommand.ExecuteAsync(null);
            await st.IntegrityCommand.ExecuteAsync(null);
            await Capture("Settings-after-backup");
            var backup = Directory.GetFiles(reportDirectory, "gpc-backup-*.db").FirstOrDefault();
            _details["settingsStatus"] = st.StatusText;
            return profileSaved && backup is not null && ProfitStore.ValidateBackup(backup).IsValid
                   && Directory.GetFiles(reportDirectory, "gpc-ledger-*.json").Length > 0 && st.StatusText.Contains("정상", StringComparison.Ordinal);
        });

        await Check("dashboard", async () =>
        {
            main.CurrentPage = main.Dashboard;
            await main.Dashboard.RefreshAsync();
            await Task.Delay(1000);
            await Capture("Dashboard-final");
            return main.Dashboard.Totals.Count == 2 && main.Dashboard.Gpus.Count > 0;
        });

        await Check("font-scale", async () =>
        {
            ((App)Application.Current).ApplyFontScale(1.3);
            await Task.Delay(500);
            await Capture("Dashboard-130-percent");
            ((App)Application.Current).ApplyFontScale(1.0);
            return true;
        });
    }

    private void WriteReport()
    {
        var pathErrors = _bindings.Errors.Where(e => e.Contains("BindingExpression path error", StringComparison.Ordinal)).ToList();
        _checks["no-exceptions"] = _exceptions.Count == 0;
        _checks["no-binding-path-errors"] = pathErrors.Count == 0;
        var ok = _checks.Values.All(v => v);
        ExitCode = ok ? 0 : 1;
        object? stats = null;
        string? integrity = null;
        try
        {
            stats = engine.Store.GetStats();
            integrity = engine.Store.IntegrityCheck();
        }
        catch (Exception ex)
        {
            RecordException(ex);
        }

        var report = new
        {
            ok,
            checks = _checks,
            details = _details,
            elapsedSeconds = _clock.Elapsed.TotalSeconds,
            isMockSensors = engine.IsMock,
            gpuProvider = engine.Hardware?.Gpu.Name,
            gpuAvailable = engine.Hardware?.Gpu.IsAvailable,
            gpuUnavailableReason = engine.Hardware?.Gpu.UnavailableReason,
            gpus = engine.Hardware?.Info.Gpus.Select(g => new { g.Index, g.Name, g.MemoryTotalBytes, g.PowerLimitW }),
            cpu = engine.Hardware?.Info.CpuName,
            driver = engine.Hardware?.Info.GpuDriverVersion,
            elevated = Environment.IsPrivilegedProcess,
            database = stats,
            integrity,
            recoveredRuns = engine.RecoveredRuns.Select(r => new { r.Id, r.Started, r.Ended, r.EndReason }),
            recoveredSessions = engine.RecoveredSessions.Select(s => new { s.Id, s.State, s.Ended }),
            screenshots = _screens,
            exceptions = _exceptions,
            bindingPathErrors = pathErrors,
            otherBindingMessages = _bindings.Errors.Except(pathErrors).Take(50),
            notices = main.Notices,
            pageStatus = main.Pages.ToDictionary(p => p.Title, p => p.StatusText),
            dialogs = dialogs.Log,
        };
        File.WriteAllText(Path.Combine(reportDirectory, "smoke-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var end = _clock.Elapsed + timeout;
        while (_clock.Elapsed < end)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return condition();
    }

    private async Task Capture(string name)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var width = Math.Max(1, (int)(window.ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)(window.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var file = Path.Combine(reportDirectory, $"{_screens.Count + 1:00}-{name}.png");
            await using (var stream = File.Create(file))
            {
                encoder.Save(stream);
            }

            _screens.Add(Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            RecordException(ex);
        }
    }

    private sealed class BindingErrorListener : TraceListener
    {
        private readonly object _gate = new();
        private readonly List<string> _errors = [];

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (_gate)
                {
                    return [.. _errors];
                }
            }
        }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_errors.Count < 500)
                {
                    _errors.Add(message);
                }
            }
        }
    }
}
