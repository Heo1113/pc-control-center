using System.ComponentModel;
using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PcControlCenter.Presentation;

namespace PcControlCenter.App.Views;

/// <summary>
/// Shows Basalt's own dashboard (http://127.0.0.1:port/) in WebView2 while Basalt answers, and what to do otherwise. The
/// WebView2 control (and its msedgewebview2 processes) exists only while the dashboard is shown: it is created when Basalt
/// answers and the page is on screen, and disposed when Basalt stops or the page is left. Only Basalt's own address opens
/// inside the app; other http(s) links open in the default browser, new windows are not opened.
/// </summary>
public partial class BasaltView : UserControl
{
    private BasaltViewModel? _vm;
    private WebView2? _web;
    private bool _creating;

    public BasaltView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as BasaltViewModel);
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => DisposeWeb();
    }

    /// <summary>Smoke test: the dashboard as WebView2 renders it (WPF cannot capture the WebView2 window). False when none is shown.</summary>
    internal async Task<bool> CaptureDashboardAsync(string path)
    {
        if (_web?.CoreWebView2 is not { } core)
        {
            return false;
        }

        await using var file = File.Create(path);
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
        return true;
    }

    /// <summary>Smoke test: the dashboard finished loading Basalt's address.</summary>
    internal string? DashboardSource => _web?.CoreWebView2 is not null ? _web.Source?.AbsoluteUri : null;

    private void Attach(BasaltViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = vm;
        if (vm is not null)
        {
            vm.PropertyChanged += OnViewModelChanged;
        }

        Sync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BasaltViewModel.ShowDashboard) or nameof(BasaltViewModel.DashboardUrl) or nameof(BasaltViewModel.WebViewProblem))
        {
            Sync();
        }
    }

    private void Sync()
    {
        var show = IsLoaded && _vm is { ShowDashboard: true, WebViewProblem: null };
        Guidance.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        WebHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            _ = EnsureWebAsync();
        }
        else
        {
            DisposeWeb();
        }
    }

    private async Task EnsureWebAsync()
    {
        if (_vm is not { } vm || _creating)
        {
            return;
        }

        var target = new Uri(vm.DashboardUrl);
        if (_web is not null)
        {
            if (_web.CoreWebView2 is not null && _web.Source != target)
            {
                _web.Source = target;
            }

            return;
        }

        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            vm.WebViewProblem = "Microsoft Edge WebView2 런타임이 없어 대시보드를 이 창에 표시할 수 없습니다. [브라우저에서 열기]로 보거나, WebView2 런타임을 설치하세요 "
                                + "(Windows 11에는 기본으로 들어 있습니다).";
            return;
        }

        _creating = true;
        var web = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = vm.WebViewDataFolder },
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x12, 0x12, 0x11),
        };
        _web = web;
        WebHost.Child = web;
        try
        {
            await web.EnsureCoreWebView2Async();
            if (!ReferenceEquals(_web, web))
            {
                return; // disposed while starting
            }

            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.ProcessFailed += OnProcessFailed;
            web.Source = target;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or WebView2RuntimeNotFoundException or UnauthorizedAccessException or IOException)
        {
            if (ReferenceEquals(_web, web))
            {
                DisposeWeb();
                vm.WebViewProblem = "WebView2를 시작하지 못해 대시보드를 표시할 수 없습니다: " + ex.Message;
            }
        }
        finally
        {
            _creating = false;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_vm is null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || _vm.IsDashboardAddress(uri))
        {
            return;
        }

        e.Cancel = true;
        if (e.IsUserInitiated)
        {
            _vm.OpenOutside(uri);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // no second browser window inside the app
        if (_vm is not null && e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            if (_vm.IsDashboardAddress(uri) && _web is not null)
            {
                _web.Source = uri;
            }
            else
            {
                _vm.OpenOutside(uri);
            }
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            Dispatcher.BeginInvoke(() =>
            {
                DisposeWeb();
                Sync(); // a new WebView2 if Basalt still answers
            });
        }
    }

    private void DisposeWeb()
    {
        if (_web is not { } web)
        {
            return;
        }

        _web = null;
        WebHost.Child = null;
        if (web.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.ProcessFailed -= OnProcessFailed;
        }

        web.Dispose();
    }
}
