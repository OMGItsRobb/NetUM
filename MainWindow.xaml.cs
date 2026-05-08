using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System;
using System.Collections.Generic;

namespace NetUM;

public sealed partial class MainWindow : Window
{
    private readonly NetworkMonitor _monitor = new();
    private readonly UsageTracker _tracker = new();
    private readonly DispatcherTimer _timer;
    private HistoryWindow? _historyWindow;
    private TrayIconController? _trayIcon;
    private bool _lifetimeInitialized;

    public MainWindow()
    {
        InitializeComponent();

        AppWindow.Resize(new Windows.Graphics.SizeInt32(400, 580));
        Title = "NetUM";

        var iconPath = TrayIconController.GetIconPath();
        if (!string.IsNullOrEmpty(iconPath))
            AppWindow.SetIcon(iconPath);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        _monitor.Update();
        UpdateUI();

        _timer.Start();
        Closed += OnWindowClosed;

        StartupToggle.IsOn = IsStartupEnabled();
    }

    public void InitializeLifetime()
    {
        if (_lifetimeInitialized)
            return;

        _lifetimeInitialized = true;
        _trayIcon = new TrayIconController(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            RestoreFromTray,
            ExitApplication);
    }

    private void OnTick(object? sender, object e)
    {
        _monitor.Update();
        var alerts = _tracker.AddUsage(
            _monitor.BytesReceivedDelta,
            _monitor.BytesSentDelta,
            _monitor.AdapterDeltas);

        UpdateUI();
        ShowAlerts(alerts);

        if (_historyWindow is not null)
            _historyWindow.RefreshIfVisible();
    }

    private void UpdateUI()
    {
        DownloadSpeedText.Text = Formatter.FormatSpeed(_monitor.DownloadSpeedMbps);
        UploadSpeedText.Text = Formatter.FormatSpeed(_monitor.UploadSpeedMbps);
        TodayDownText.Text = Formatter.FormatBytes(_tracker.TodayBytesReceived);
        TodayUpText.Text = Formatter.FormatBytes(_tracker.TodayBytesSent);

        var month = _tracker.GetCurrentMonthTotals();
        MonthDownText.Text = Formatter.FormatBytes(month.bytesReceived);
        MonthUpText.Text = Formatter.FormatBytes(month.bytesSent);
    }

    private void ShowAlerts(IReadOnlyList<UsageAlert> alerts)
    {
        if (alerts.Count == 0) return;

        foreach (var alert in alerts)
        {
            AlertBar.Title = alert.Kind == UsageAlertKind.DailyCapExceeded
                ? "Daily Cap Alert" : "Monthly Cap Alert";
            AlertBar.Message = alert.Message;
            AlertBar.IsOpen = true;
        }
    }

    private void OnAlwaysOnTopToggled(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = AlwaysOnTopToggle.IsOn;
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        SetStartup(StartupToggle.IsOn);
    }

    private void OnShowHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_historyWindow == null)
        {
            _historyWindow = new HistoryWindow(_tracker);
            _historyWindow.Closed += (_, _) => _historyWindow = null;
        }
        _historyWindow.Activate();
    }

    private async void OnResetDataClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset NetUM Data",
            Content = "This will permanently clear all usage history and cap settings. Continue?",
            PrimaryButtonText = "Yes, Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _tracker.ResetAllData();
            UpdateUI();
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        _tracker.FlushNow();
        _historyWindow?.Close();
        _trayIcon?.Dispose();
    }

    private void RestoreFromTray()
    {
        Activate();
    }

    private void ExitApplication()
    {
        _trayIcon?.RequestExit();
        Close();
    }

    // ── Startup registry ──────────────────────────────────────────────────

    private const string StartupKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "NetUM";

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
            return key?.GetValue(AppName) != null;
        }
        catch { return false; }
    }

    private static void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey, writable: true);
            if (key == null) return;
            if (enable)
                key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch { /* Ignore registry errors. */ }
    }
}
