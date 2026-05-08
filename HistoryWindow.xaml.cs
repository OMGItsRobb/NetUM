using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NetUM;

public sealed partial class HistoryWindow : Window
{
    private readonly UsageTracker _tracker;
    private readonly ObservableCollection<HistoryDisplayItem> _historyItems = [];
    private readonly ObservableCollection<AdapterDisplayItem> _adapterItems = [];
    private List<HistoryDisplayItem> _historySource = new();

    private string _sortColumn = "Period";
    private bool _sortAscending;
    private bool _initialized;

    private UsageGrouping CurrentGrouping => GroupByCombo.SelectedIndex switch
    {
        1 => UsageGrouping.Week,
        2 => UsageGrouping.Month,
        _ => UsageGrouping.Day
    };

    public HistoryWindow(UsageTracker tracker)
    {
        _tracker = tracker;
        InitializeComponent();

        Title = "NetUM — Usage History";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));

        HistoryList.ItemsSource = _historyItems;
        AdapterList.ItemsSource = _adapterItems;

        GroupByCombo.SelectedIndex = 0;
        RangeCombo.SelectedIndex = 1;

        LoadCapSettings();
        _initialized = true;
        RefreshData();
    }

    public void RefreshIfVisible()
    {
        if (AppWindow.IsVisible)
            RefreshData();
    }

    public void RefreshData()
    {
        if (!_initialized) return;

        var grouping = CurrentGrouping;
        var history = _tracker.GetHistory(grouping, descending: false);

        int? take = RangeCombo.SelectedIndex switch
        {
            0 => 7,
            1 => 30,
            2 => 90,
            _ => null
        };

        if (take.HasValue && history.Count > take.Value)
            history = history.TakeLast(take.Value).ToList();

        _historySource = history.Select(h => new HistoryDisplayItem
        {
            Period = h.PeriodLabel,
            Downloaded = Formatter.FormatBytes(h.BytesReceived),
            Uploaded = Formatter.FormatBytes(h.BytesSent),
            Total = Formatter.FormatBytes(h.TotalBytes),
            BytesReceived = h.BytesReceived,
            BytesSent = h.BytesSent,
            PeriodStart = h.PeriodStart,
            PeriodEnd = h.PeriodEnd
        }).ToList();

        ApplySortAndRender();
        RefreshAdapters(history);
        DrawChart();
    }

    // ── Sorting ───────────────────────────────────────────────────────────

    private void ApplySortAndRender()
    {
        var sorted = (_sortColumn switch
        {
            "Downloaded" => _sortAscending
                ? _historySource.OrderBy(x => x.BytesReceived)
                : _historySource.OrderByDescending(x => x.BytesReceived),
            "Uploaded" => _sortAscending
                ? _historySource.OrderBy(x => x.BytesSent)
                : _historySource.OrderByDescending(x => x.BytesSent),
            "Total" => _sortAscending
                ? _historySource.OrderBy(x => x.BytesReceived + x.BytesSent)
                : _historySource.OrderByDescending(x => x.BytesReceived + x.BytesSent),
            _ => _sortAscending
                ? _historySource.OrderBy(x => x.PeriodStart)
                : _historySource.OrderByDescending(x => x.PeriodStart)
        }).ToList();

        SyncHistoryItems(sorted);
        UpdateSummary();
        UpdateSortGlyphs();
    }

    private void ToggleSort(string column)
    {
        if (_sortColumn == column)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = false;
        }
        ApplySortAndRender();
    }

    private void UpdateSortGlyphs()
    {
        string arrow = _sortAscending ? " ▲" : " ▼";
        SortPeriodBtn.Content = "Period" + (_sortColumn == "Period" ? arrow : "");
        SortDownBtn.Content = "Downloaded" + (_sortColumn == "Downloaded" ? arrow : "");
        SortUpBtn.Content = "Uploaded" + (_sortColumn == "Uploaded" ? arrow : "");
        SortTotalBtn.Content = "Total" + (_sortColumn == "Total" ? arrow : "");
    }

    // ── Adapters ──────────────────────────────────────────────────────────

    private void RefreshAdapters(IReadOnlyList<UsagePeriodStats> history)
    {
        if (history.Count == 0)
        {
            _adapterItems.Clear();
            AdapterSummaryText.Text = "No data";
            return;
        }

        var start = history.Min(x => x.PeriodStart.Date);
        var end = history.Max(x => x.PeriodEnd.Date);
        var stats = _tracker.GetAdapterTotals(start, end);
        long grandTotal = stats.Sum(x => x.TotalBytes);

        var adapterItems = stats.Select(a => new AdapterDisplayItem
        {
            AdapterName = a.AdapterName,
            AdapterType = a.AdapterType,
            Downloaded = Formatter.FormatBytes(a.BytesReceived),
            Uploaded = Formatter.FormatBytes(a.BytesSent),
            Total = Formatter.FormatBytes(a.TotalBytes),
            Share = grandTotal > 0 ? $"{a.TotalBytes * 100.0 / grandTotal:F1}%" : "0.0%",
            BytesReceived = a.BytesReceived,
            BytesSent = a.BytesSent
        }).ToList();

        SyncAdapterItems(adapterItems);
        AdapterSummaryText.Text =
            $"Adapter totals for {start:yyyy-MM-dd} to {end:yyyy-MM-dd}: {Formatter.FormatBytes(grandTotal)}";
    }

    private void SyncHistoryItems(IReadOnlyList<HistoryDisplayItem> items)
    {
        SyncItems(_historyItems, items, static (current, updated) => current.CopyFrom(updated));
    }

    private void SyncAdapterItems(IReadOnlyList<AdapterDisplayItem> items)
    {
        SyncItems(_adapterItems, items, static (current, updated) => current.CopyFrom(updated));
    }

    private static void SyncItems<T>(
        ObservableCollection<T> currentItems,
        IReadOnlyList<T> updatedItems,
        Action<T, T> applyUpdate)
    {
        for (var index = 0; index < updatedItems.Count; index++)
        {
            if (index < currentItems.Count)
            {
                applyUpdate(currentItems[index], updatedItems[index]);
                continue;
            }

            currentItems.Add(updatedItems[index]);
        }

        while (currentItems.Count > updatedItems.Count)
            currentItems.RemoveAt(currentItems.Count - 1);
    }

    // ── Summary ───────────────────────────────────────────────────────────

    private void UpdateSummary()
    {
        if (_historyItems.Count == 0)
        {
            SummaryText.Text = "Totals: no data";
            AverageText.Text = "Average: —";
            PeakText.Text = "Peak: —";
            return;
        }

        long totalDown = _historyItems.Sum(x => x.BytesReceived);
        long totalUp = _historyItems.Sum(x => x.BytesSent);
        long total = totalDown + totalUp;
        long average = total / _historyItems.Count;
        var peak = _historyItems.OrderByDescending(x => x.BytesReceived + x.BytesSent).First();

        SummaryText.Text = $"Down {Formatter.FormatBytes(totalDown)}   " +
                           $"Up {Formatter.FormatBytes(totalUp)}   " +
                           $"Total {Formatter.FormatBytes(total)}";
        AverageText.Text = $"Avg/period: {Formatter.FormatBytes(average)}";
        PeakText.Text = $"Peak: {peak.Period} ({Formatter.FormatBytes(peak.BytesReceived + peak.BytesSent)})";
    }

    // ── Chart ─────────────────────────────────────────────────────────────

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();

        var data = _historyItems.OrderBy(x => x.PeriodStart).ToList();
        if (data.Count == 0) return;
        if (data.Count > 60) data = data.TakeLast(60).ToList();

        double cw = ChartCanvas.ActualWidth;
        double ch = ChartCanvas.ActualHeight;
        if (cw < 50 || ch < 50) return;

        const double lm = 50, rm = 10, tm = 10, bm = 30;
        double pw = cw - lm - rm;
        double ph = ch - tm - bm;
        if (pw < 10 || ph < 10) return;

        // Rolling averages
        var rolling = new List<double>(data.Count);
        for (int i = 0; i < data.Count; i++)
        {
            int s = Math.Max(0, i - 6);
            double avg = data.Skip(s).Take(i - s + 1)
                .Average(x => (double)(x.BytesReceived + x.BytesSent));
            rolling.Add(avg);
        }

        double maxVal = Math.Max(1.0,
            Math.Max(data.Max(x => (double)(x.BytesReceived + x.BytesSent)), rolling.Max()));

        var downBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 72, 214, 96));
        var upBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 165, 50));
        var avgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 180, 255));
        var gridBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 42, 42, 58));
        var dimBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 120, 140));

        // Grid lines
        for (int i = 1; i <= 3; i++)
        {
            double y = tm + ph - (ph * i / 4.0);
            var line = new Line
            {
                X1 = lm, Y1 = y, X2 = lm + pw, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1
            };
            ChartCanvas.Children.Add(line);
        }

        double step = pw / data.Count;
        double bw = Math.Max(3, step - 2);

        // Bars
        for (int i = 0; i < data.Count; i++)
        {
            double downH = data[i].BytesReceived / maxVal * ph;
            double upH = data[i].BytesSent / maxVal * ph;
            double x = lm + i * step + (step - bw) / 2;

            if (downH > 0.5)
            {
                var rect = new Rectangle { Width = bw, Height = downH, Fill = downBrush };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, tm + ph - downH);
                ChartCanvas.Children.Add(rect);
            }
            if (upH > 0.5)
            {
                var rect = new Rectangle { Width = bw, Height = upH, Fill = upBrush };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, tm + ph - downH - upH);
                ChartCanvas.Children.Add(rect);
            }
        }

        // Rolling average line
        var points = new PointCollection();
        for (int i = 0; i < rolling.Count; i++)
        {
            double x = lm + i * step + step / 2;
            double y = tm + ph - (rolling[i] / maxVal * ph);
            points.Add(new Point(x, y));
        }
        if (points.Count >= 2)
        {
            ChartCanvas.Children.Add(new Polyline
            {
                Stroke = avgBrush, StrokeThickness = 2, Points = points
            });
        }

        // Y-axis labels
        var maxLabel = new TextBlock
        {
            Text = Formatter.FormatBytes((long)maxVal), FontSize = 10, Foreground = dimBrush
        };
        Canvas.SetLeft(maxLabel, 2);
        Canvas.SetTop(maxLabel, tm);
        ChartCanvas.Children.Add(maxLabel);

        var zeroLabel = new TextBlock { Text = "0", FontSize = 10, Foreground = dimBrush };
        Canvas.SetLeft(zeroLabel, 30);
        Canvas.SetTop(zeroLabel, tm + ph - 14);
        ChartCanvas.Children.Add(zeroLabel);

        // X-axis labels
        int labelStep = Math.Max(1, data.Count / 10);
        for (int i = 0; i < data.Count; i += labelStep)
        {
            string label = CurrentGrouping switch
            {
                UsageGrouping.Month => data[i].PeriodStart.ToString("yy-MM"),
                UsageGrouping.Week => data[i].Period,
                _ => data[i].PeriodStart.ToString("MM-dd")
            };
            var tb = new TextBlock { Text = label, FontSize = 10, Foreground = dimBrush };
            Canvas.SetLeft(tb, lm + i * step);
            Canvas.SetTop(tb, tm + ph + 4);
            ChartCanvas.Children.Add(tb);
        }

        // Legend
        var legendDown = new TextBlock
        {
            Text = "Stacked: down/up", FontSize = 10, Foreground = dimBrush
        };
        Canvas.SetLeft(legendDown, lm + pw - 180);
        Canvas.SetTop(legendDown, tm + 4);
        ChartCanvas.Children.Add(legendDown);

        var legendAvg = new TextBlock
        {
            Text = "Line: 7-period rolling avg", FontSize = 10, Foreground = avgBrush
        };
        Canvas.SetLeft(legendAvg, lm + pw - 180);
        Canvas.SetTop(legendAvg, tm + 20);
        ChartCanvas.Children.Add(legendAvg);
    }

    // ── Cap Settings ──────────────────────────────────────────────────────

    private void LoadCapSettings()
    {
        var caps = _tracker.GetCapSettings();
        DailyCapInput.Value = caps.DailyCapBytes > 0
            ? Math.Round(caps.DailyCapBytes / 1_073_741_824.0, 2) : 0;
        MonthlyCapInput.Value = caps.MonthlyCapBytes > 0
            ? Math.Round(caps.MonthlyCapBytes / 1_073_741_824.0, 2) : 0;
        UpdateCapStatus(caps);
    }

    private void OnSaveCapsClick(object sender, RoutedEventArgs e)
    {
        double dailyGb = double.IsNaN(DailyCapInput.Value) ? 0 : DailyCapInput.Value;
        double monthlyGb = double.IsNaN(MonthlyCapInput.Value) ? 0 : MonthlyCapInput.Value;

        var caps = new UsageCapSettings
        {
            DailyCapBytes = (long)(dailyGb * 1_073_741_824),
            MonthlyCapBytes = (long)(monthlyGb * 1_073_741_824)
        };
        _tracker.UpdateCapSettings(caps);
        UpdateCapStatus(caps);
    }

    private void UpdateCapStatus(UsageCapSettings caps)
    {
        if (caps.DailyCapBytes <= 0 && caps.MonthlyCapBytes <= 0)
        {
            CapStatusText.Text = "Caps: disabled";
            return;
        }
        var daily = caps.DailyCapBytes > 0 ? Formatter.FormatBytes(caps.DailyCapBytes) : "off";
        var monthly = caps.MonthlyCapBytes > 0 ? Formatter.FormatBytes(caps.MonthlyCapBytes) : "off";
        CapStatusText.Text = $"Daily {daily}, Monthly {monthly}";
    }

    // ── CSV Export ────────────────────────────────────────────────────────

    private async void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        bool isAdapterTab = ContentPivot.SelectedIndex == 1;
        picker.SuggestedFileName =
            $"netum-{(isAdapterTab ? "adapters" : "history")}-{DateTime.Now:yyyyMMdd-HHmmss}";
        picker.FileTypeChoices.Add("CSV files", new List<string> { ".csv" });

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var sb = new StringBuilder();
        if (isAdapterTab)
        {
            sb.AppendLine("Adapter,Type,DownloadedBytes,UploadedBytes,TotalBytes,Share");
            foreach (var r in _adapterItems)
                sb.AppendLine(
                    $"{Escape(r.AdapterName)},{Escape(r.AdapterType)}," +
                    $"{r.BytesReceived},{r.BytesSent},{r.BytesReceived + r.BytesSent},{r.Share}");
        }
        else
        {
            sb.AppendLine("Period,DownloadedBytes,UploadedBytes,TotalBytes");
            foreach (var r in _historyItems.OrderBy(x => x.PeriodStart))
                sb.AppendLine(
                    $"{r.Period},{r.BytesReceived},{r.BytesSent},{r.BytesReceived + r.BytesSent}");
        }

        await Windows.Storage.FileIO.WriteTextAsync(file, sb.ToString());
    }

    private static string Escape(string v)
    {
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
            return $"\"{v.Replace("\"", "\"\"")}\"";
        return v;
    }

    // ── Reset ─────────────────────────────────────────────────────────────

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset NetUM Data",
            Content = "This will remove all recorded usage history and cap settings. Continue?",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _tracker.ResetAllData();
            LoadCapSettings();
            RefreshData();
        }
    }

    // ── Event Handlers ────────────────────────────────────────────────────

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => RefreshData();
    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshData();
    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();
    private void OnSortPeriod(object sender, RoutedEventArgs e) => ToggleSort("Period");
    private void OnSortDown(object sender, RoutedEventArgs e) => ToggleSort("Downloaded");
    private void OnSortUp(object sender, RoutedEventArgs e) => ToggleSort("Uploaded");
    private void OnSortTotal(object sender, RoutedEventArgs e) => ToggleSort("Total");
}

// ── Display Item Classes ──────────────────────────────────────────────────

public class HistoryDisplayItem : BindableDisplayItem
{
    private string _period = "";
    private string _downloaded = "";
    private string _uploaded = "";
    private string _total = "";
    private long _bytesReceived;
    private long _bytesSent;
    private DateTime _periodStart;
    private DateTime _periodEnd;

    public string Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public string Downloaded
    {
        get => _downloaded;
        set => SetProperty(ref _downloaded, value);
    }

    public string Uploaded
    {
        get => _uploaded;
        set => SetProperty(ref _uploaded, value);
    }

    public string Total
    {
        get => _total;
        set => SetProperty(ref _total, value);
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        set => SetProperty(ref _bytesReceived, value);
    }

    public long BytesSent
    {
        get => _bytesSent;
        set => SetProperty(ref _bytesSent, value);
    }

    public DateTime PeriodStart
    {
        get => _periodStart;
        set => SetProperty(ref _periodStart, value);
    }

    public DateTime PeriodEnd
    {
        get => _periodEnd;
        set => SetProperty(ref _periodEnd, value);
    }

    public void CopyFrom(HistoryDisplayItem other)
    {
        Period = other.Period;
        Downloaded = other.Downloaded;
        Uploaded = other.Uploaded;
        Total = other.Total;
        BytesReceived = other.BytesReceived;
        BytesSent = other.BytesSent;
        PeriodStart = other.PeriodStart;
        PeriodEnd = other.PeriodEnd;
    }
}

public class AdapterDisplayItem : BindableDisplayItem
{
    private string _adapterName = "";
    private string _adapterType = "";
    private string _downloaded = "";
    private string _uploaded = "";
    private string _total = "";
    private string _share = "";
    private long _bytesReceived;
    private long _bytesSent;

    public string AdapterName
    {
        get => _adapterName;
        set => SetProperty(ref _adapterName, value);
    }

    public string AdapterType
    {
        get => _adapterType;
        set => SetProperty(ref _adapterType, value);
    }

    public string Downloaded
    {
        get => _downloaded;
        set => SetProperty(ref _downloaded, value);
    }

    public string Uploaded
    {
        get => _uploaded;
        set => SetProperty(ref _uploaded, value);
    }

    public string Total
    {
        get => _total;
        set => SetProperty(ref _total, value);
    }

    public string Share
    {
        get => _share;
        set => SetProperty(ref _share, value);
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        set => SetProperty(ref _bytesReceived, value);
    }

    public long BytesSent
    {
        get => _bytesSent;
        set => SetProperty(ref _bytesSent, value);
    }

    public void CopyFrom(AdapterDisplayItem other)
    {
        AdapterName = other.AdapterName;
        AdapterType = other.AdapterType;
        Downloaded = other.Downloaded;
        Uploaded = other.Uploaded;
        Total = other.Total;
        Share = other.Share;
        BytesReceived = other.BytesReceived;
        BytesSent = other.BytesSent;
    }
}

public abstract class BindableDisplayItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
