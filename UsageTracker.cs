using System.Globalization;
using System.Text.Json;

namespace NetUM;

public enum UsageGrouping
{
    Day,
    Week,
    Month
}

public enum UsageAlertKind
{
    DailyCapExceeded,
    MonthlyCapExceeded
}

public sealed class UsageAlert
{
    public UsageAlertKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class UsageCapSettings
{
    public long DailyCapBytes { get; init; }
    public long MonthlyCapBytes { get; init; }
}

public sealed class UsagePeriodStats
{
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public string PeriodLabel { get; init; } = string.Empty;
    public long BytesReceived { get; init; }
    public long BytesSent { get; init; }
    public long TotalBytes => BytesReceived + BytesSent;
}

public sealed class AdapterUsageStats
{
    public string AdapterId { get; init; } = string.Empty;
    public string AdapterName { get; init; } = string.Empty;
    public string AdapterType { get; init; } = string.Empty;
    public long BytesReceived { get; init; }
    public long BytesSent { get; init; }
    public long TotalBytes => BytesReceived + BytesSent;
}

/// <summary>
/// Accumulates bytes transferred and persists full daily history to
/// %APPDATA%\NetUM\usage-history.json.
/// </summary>
public class UsageTracker
{
    private readonly string _dataFilePath;
    private readonly string _legacyDataFilePath;
    private readonly SortedDictionary<DateOnly, DailyUsageRecord> _history = new();

    private DateOnly _currentDay;
    private long _dailyBytesReceived;
    private long _dailyBytesSent;
    private int _saveCounter;
    private UsageCapSettings _capSettings = new();
    private DateOnly? _lastDailyCapAlertDate;
    private string? _lastMonthlyCapAlertKey;

    public long TodayBytesReceived => _dailyBytesReceived;
    public long TodayBytesSent => _dailyBytesSent;

    public UsageTracker()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetUM");
        Directory.CreateDirectory(dir);
        _dataFilePath = Path.Combine(dir, "usage-history.json");
        _legacyDataFilePath = Path.Combine(dir, "usage.json");
        Load();
    }

    public IReadOnlyList<UsageAlert> AddUsage(
        long bytesReceived,
        long bytesSent,
        IReadOnlyList<NetworkAdapterDelta>? adapterDeltas = null)
    {
        RollToTodayIfNeeded();

        _dailyBytesReceived += Math.Max(0L, bytesReceived);
        _dailyBytesSent += Math.Max(0L, bytesSent);

        var todayRecord = GetOrCreateDayRecord(_currentDay);
        todayRecord.BytesReceived = _dailyBytesReceived;
        todayRecord.BytesSent = _dailyBytesSent;

        if (adapterDeltas != null)
        {
            foreach (var adapterDelta in adapterDeltas)
            {
                var down = Math.Max(0L, adapterDelta.BytesReceivedDelta);
                var up = Math.Max(0L, adapterDelta.BytesSentDelta);
                if (down == 0 && up == 0)
                    continue;

                if (!todayRecord.Adapters.TryGetValue(adapterDelta.AdapterId, out var adapterRecord))
                {
                    adapterRecord = new AdapterUsageRecord
                    {
                        AdapterId = adapterDelta.AdapterId,
                        AdapterName = adapterDelta.AdapterName,
                        AdapterType = adapterDelta.AdapterType
                    };
                    todayRecord.Adapters[adapterDelta.AdapterId] = adapterRecord;
                }

                adapterRecord.AdapterName = adapterDelta.AdapterName;
                adapterRecord.AdapterType = adapterDelta.AdapterType;
                adapterRecord.BytesReceived += down;
                adapterRecord.BytesSent += up;
            }
        }

        var alerts = EvaluateCapAlerts();

        // Flush to disk every 10 updates (~10 s) to limit I/O.
        if (++_saveCounter >= 10 || alerts.Count > 0)
        {
            _saveCounter = 0;
            Save();
        }

        return alerts;
    }

    /// <summary>Force an immediate save (e.g. on application exit).</summary>
    public void FlushNow() => Save();

    public IReadOnlyList<UsagePeriodStats> GetHistory(UsageGrouping grouping, bool descending = true)
    {
        RollToTodayIfNeeded();
        PersistCurrentDayTotals();

        IEnumerable<UsagePeriodStats> query = grouping switch
        {
            UsageGrouping.Day => _history.Select(pair => new UsagePeriodStats
            {
                PeriodStart = pair.Key.ToDateTime(TimeOnly.MinValue),
                PeriodEnd = pair.Key.ToDateTime(TimeOnly.MinValue),
                PeriodLabel = pair.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                BytesReceived = pair.Value.BytesReceived,
                BytesSent = pair.Value.BytesSent
            }),
            UsageGrouping.Week => _history
                .GroupBy(pair => GetWeekStart(pair.Key))
                .Select(group =>
                {
                    var weekStart = group.Key;
                    var weekEnd = weekStart.AddDays(6);
                    var weekStartDate = weekStart.ToDateTime(TimeOnly.MinValue);
                    var isoYear = ISOWeek.GetYear(weekStartDate);
                    var isoWeek = ISOWeek.GetWeekOfYear(weekStartDate);

                    return new UsagePeriodStats
                    {
                        PeriodStart = weekStartDate,
                        PeriodEnd = weekEnd.ToDateTime(TimeOnly.MinValue),
                        PeriodLabel = $"{isoYear}-W{isoWeek:D2}",
                        BytesReceived = group.Sum(item => item.Value.BytesReceived),
                        BytesSent = group.Sum(item => item.Value.BytesSent)
                    };
                }),
            _ => _history
                .GroupBy(pair => new { pair.Key.Year, pair.Key.Month })
                .Select(group => new UsagePeriodStats
                {
                    PeriodStart = new DateTime(group.Key.Year, group.Key.Month, 1),
                    PeriodEnd = new DateTime(group.Key.Year, group.Key.Month, 1).AddMonths(1).AddDays(-1),
                    PeriodLabel = $"{group.Key.Year:D4}-{group.Key.Month:D2}",
                    BytesReceived = group.Sum(item => item.Value.BytesReceived),
                    BytesSent = group.Sum(item => item.Value.BytesSent)
                })
        };

        query = descending
            ? query.OrderByDescending(item => item.PeriodStart)
            : query.OrderBy(item => item.PeriodStart);

        return query.ToList();
    }

    public (long bytesReceived, long bytesSent) GetCurrentMonthTotals()
    {
        RollToTodayIfNeeded();
        PersistCurrentDayTotals();

        return GetMonthTotals(_currentDay);
    }

    public UsageCapSettings GetCapSettings()
    {
        return new UsageCapSettings
        {
            DailyCapBytes = _capSettings.DailyCapBytes,
            MonthlyCapBytes = _capSettings.MonthlyCapBytes
        };
    }

    public void UpdateCapSettings(UsageCapSettings settings)
    {
        _capSettings = new UsageCapSettings
        {
            DailyCapBytes = Math.Max(0L, settings.DailyCapBytes),
            MonthlyCapBytes = Math.Max(0L, settings.MonthlyCapBytes)
        };
        Save();
    }

    public IReadOnlyList<AdapterUsageStats> GetAdapterTotals(
        DateTime? fromInclusive = null,
        DateTime? toInclusive = null)
    {
        RollToTodayIfNeeded();
        PersistCurrentDayTotals();

        var fromDay = fromInclusive.HasValue ? DateOnly.FromDateTime(fromInclusive.Value.Date) : DateOnly.MinValue;
        var toDay = toInclusive.HasValue ? DateOnly.FromDateTime(toInclusive.Value.Date) : DateOnly.MaxValue;

        var totals = new Dictionary<string, AdapterTotalsBuilder>(StringComparer.Ordinal);

        foreach (var (day, usage) in _history)
        {
            if (day < fromDay || day > toDay)
                continue;

            foreach (var adapter in usage.Adapters.Values)
            {
                var adapterId = AdapterIdentity.BuildUsageGroupKey(adapter.AdapterName, adapter.AdapterType);
                if (!totals.TryGetValue(adapterId, out var acc))
                {
                    acc = new AdapterTotalsBuilder
                    {
                        AdapterId = adapterId,
                        AdapterName = AdapterIdentity.NormalizeDisplayName(adapter.AdapterName),
                        AdapterType = adapter.AdapterType
                    };
                    totals[adapterId] = acc;
                }

                acc.AdapterName = AdapterIdentity.NormalizeDisplayName(adapter.AdapterName);
                acc.AdapterType = adapter.AdapterType;
                acc.BytesReceived += adapter.BytesReceived;
                acc.BytesSent += adapter.BytesSent;
            }
        }

        return totals.Values
            .Select(builder => new AdapterUsageStats
            {
                AdapterId = builder.AdapterId,
                AdapterName = builder.AdapterName,
                AdapterType = builder.AdapterType,
                BytesReceived = builder.BytesReceived,
                BytesSent = builder.BytesSent
            })
            .OrderByDescending(item => item.TotalBytes)
            .ToList();
    }

    public void ResetAllData()
    {
        _history.Clear();
        _currentDay = DateOnly.FromDateTime(DateTime.Today);
        _dailyBytesReceived = 0;
        _dailyBytesSent = 0;
        _saveCounter = 0;
        _capSettings = new UsageCapSettings();
        _lastDailyCapAlertDate = null;
        _lastMonthlyCapAlertKey = null;

        _history[_currentDay] = new DailyUsageRecord();
        Save();

        try
        {
            if (File.Exists(_legacyDataFilePath))
                File.Delete(_legacyDataFilePath);
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private void RollToTodayIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (today == _currentDay)
            return;

        PersistCurrentDayTotals();
        _currentDay = today;

        if (_history.TryGetValue(_currentDay, out var existing))
        {
            _dailyBytesReceived = existing.BytesReceived;
            _dailyBytesSent = existing.BytesSent;
        }
        else
        {
            _dailyBytesReceived = 0;
            _dailyBytesSent = 0;
        }
    }

    private void PersistCurrentDayTotals()
    {
        var record = GetOrCreateDayRecord(_currentDay);
        record.BytesReceived = _dailyBytesReceived;
        record.BytesSent = _dailyBytesSent;
    }

    private DailyUsageRecord GetOrCreateDayRecord(DateOnly day)
    {
        if (_history.TryGetValue(day, out var existing))
            return existing;

        var created = new DailyUsageRecord();
        _history[day] = created;
        return created;
    }

    private static DateOnly GetWeekStart(DateOnly day)
    {
        var delta = ((int)day.DayOfWeek + 6) % 7; // Monday = 0
        return day.AddDays(-delta);
    }

    private (long bytesReceived, long bytesSent) GetMonthTotals(DateOnly forDay)
    {
        long bytesReceived = 0;
        long bytesSent = 0;

        foreach (var (day, usage) in _history)
        {
            if (day.Year == forDay.Year && day.Month == forDay.Month)
            {
                bytesReceived += usage.BytesReceived;
                bytesSent += usage.BytesSent;
            }
        }

        return (bytesReceived, bytesSent);
    }

    private List<UsageAlert> EvaluateCapAlerts()
    {
        var alerts = new List<UsageAlert>();
        var dailyTotal = _dailyBytesReceived + _dailyBytesSent;

        if (_capSettings.DailyCapBytes > 0 &&
            dailyTotal >= _capSettings.DailyCapBytes &&
            _lastDailyCapAlertDate != _currentDay)
        {
            _lastDailyCapAlertDate = _currentDay;
            alerts.Add(new UsageAlert
            {
                Kind = UsageAlertKind.DailyCapExceeded,
                Message = $"Daily cap reached: {Formatter.FormatBytes(dailyTotal)} / {Formatter.FormatBytes(_capSettings.DailyCapBytes)}"
            });
        }

        if (_capSettings.MonthlyCapBytes > 0)
        {
            var monthTotals = GetMonthTotals(_currentDay);
            var monthTotal = monthTotals.bytesReceived + monthTotals.bytesSent;
            var monthKey = _currentDay.ToString("yyyy-MM", CultureInfo.InvariantCulture);

            if (monthTotal >= _capSettings.MonthlyCapBytes &&
                !string.Equals(_lastMonthlyCapAlertKey, monthKey, StringComparison.Ordinal))
            {
                _lastMonthlyCapAlertKey = monthKey;
                alerts.Add(new UsageAlert
                {
                    Kind = UsageAlertKind.MonthlyCapExceeded,
                    Message = $"Monthly cap reached: {Formatter.FormatBytes(monthTotal)} / {Formatter.FormatBytes(_capSettings.MonthlyCapBytes)}"
                });
            }
        }

        return alerts;
    }

    private void Load()
    {
        _currentDay = DateOnly.FromDateTime(DateTime.Today);
        _dailyBytesReceived = 0;
        _dailyBytesSent = 0;
        var shouldSaveNormalizedHistory = false;

        try
        {
            if (File.Exists(_dataFilePath))
            {
                var json = File.ReadAllText(_dataFilePath);
                var data = JsonSerializer.Deserialize<UsageHistoryData>(json);
                if (data?.Daily != null)
                {
                    foreach (var row in data.Daily)
                    {
                        if (!DateOnly.TryParseExact(
                                row.Date,
                                "yyyy-MM-dd",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None,
                                out var day))
                        {
                            continue;
                        }

                        var dailyRecord = new DailyUsageRecord
                        {
                            BytesReceived = Math.Max(0L, row.BytesReceived),
                            BytesSent = Math.Max(0L, row.BytesSent),
                            Adapters = row.Adapters
                                .Where(adapter => !string.IsNullOrWhiteSpace(adapter.AdapterId))
                                .ToDictionary(
                                    adapter => adapter.AdapterId,
                                    adapter => new AdapterUsageRecord
                                    {
                                        AdapterId = adapter.AdapterId,
                                        AdapterName = adapter.AdapterName,
                                        AdapterType = adapter.AdapterType,
                                        BytesReceived = Math.Max(0L, adapter.BytesReceived),
                                        BytesSent = Math.Max(0L, adapter.BytesSent)
                                    },
                                    StringComparer.Ordinal)
                        };

                        _history[day] = NormalizeDailyRecord(dailyRecord, out var changed);
                        shouldSaveNormalizedHistory |= changed;
                    }
                }

                if (data?.Caps != null)
                {
                    _capSettings = new UsageCapSettings
                    {
                        DailyCapBytes = Math.Max(0L, data.Caps.DailyCapBytes),
                        MonthlyCapBytes = Math.Max(0L, data.Caps.MonthlyCapBytes)
                    };
                }

                if (data?.AlertState != null)
                {
                    if (DateOnly.TryParseExact(
                            data.AlertState.LastDailyCapDate,
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var lastDailyAlertDay))
                    {
                        _lastDailyCapAlertDate = lastDailyAlertDay;
                    }

                    _lastMonthlyCapAlertKey = string.IsNullOrWhiteSpace(data.AlertState.LastMonthlyCapKey)
                        ? null
                        : data.AlertState.LastMonthlyCapKey;
                }
            }
            else if (File.Exists(_legacyDataFilePath))
            {
                // One-time migration from the old single-day schema.
                var legacyJson = File.ReadAllText(_legacyDataFilePath);
                var legacy = JsonSerializer.Deserialize<LegacyUsageData>(legacyJson);
                if (legacy != null &&
                    DateOnly.TryParseExact(
                        legacy.Date,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var legacyDay))
                {
                    _history[legacyDay] = new DailyUsageRecord
                    {
                        BytesReceived = Math.Max(0L, legacy.BytesReceived),
                        BytesSent = Math.Max(0L, legacy.BytesSent)
                    };
                }
            }

            if (_history.TryGetValue(_currentDay, out var existingToday))
            {
                _dailyBytesReceived = existingToday.BytesReceived;
                _dailyBytesSent = existingToday.BytesSent;
            }

            PersistCurrentDayTotals();

            if (shouldSaveNormalizedHistory)
                Save();
        }
        catch
        {
            // Corrupted file — start fresh.
            _history.Clear();
            _dailyBytesReceived = 0;
            _dailyBytesSent = 0;
            _capSettings = new UsageCapSettings();
            _lastDailyCapAlertDate = null;
            _lastMonthlyCapAlertKey = null;
            PersistCurrentDayTotals();
        }
    }

    private void Save()
    {
        try
        {
            PersistCurrentDayTotals();

            var data = new UsageHistoryData
            {
                Version = 3,
                Daily = _history
                    .Select(pair => new DailyUsageData
                    {
                        Date = pair.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        BytesReceived = pair.Value.BytesReceived,
                        BytesSent = pair.Value.BytesSent,
                        Adapters = pair.Value.Adapters.Values
                            .Select(adapter => new AdapterUsageData
                            {
                                AdapterId = adapter.AdapterId,
                                AdapterName = adapter.AdapterName,
                                AdapterType = adapter.AdapterType,
                                BytesReceived = adapter.BytesReceived,
                                BytesSent = adapter.BytesSent
                            })
                            .ToList()
                    })
                    .ToList(),
                Caps = new UsageCapData
                {
                    DailyCapBytes = _capSettings.DailyCapBytes,
                    MonthlyCapBytes = _capSettings.MonthlyCapBytes
                },
                AlertState = new AlertStateData
                {
                    LastDailyCapDate = _lastDailyCapAlertDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    LastMonthlyCapKey = _lastMonthlyCapAlertKey ?? string.Empty
                }
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_dataFilePath, json);
        }
        catch
        {
            // Ignore transient file errors.
        }
    }

    private static DailyUsageRecord NormalizeDailyRecord(DailyUsageRecord record, out bool changed)
    {
        if (record.Adapters.Count == 0)
        {
            changed = false;
            return record;
        }

        var normalizedAdapters = new Dictionary<string, AdapterUsageRecord>(StringComparer.Ordinal);

        foreach (var group in record.Adapters.Values.GroupBy(
                     adapter => AdapterIdentity.BuildUsageGroupKey(adapter.AdapterName, adapter.AdapterType),
                     StringComparer.Ordinal))
        {
            var adapterType = group.First().AdapterType;
            var adapterName = AdapterIdentity.NormalizeDisplayName(group.First().AdapterName);

            long bytesReceived = 0;
            long bytesSent = 0;

            foreach (var counterGroup in group.GroupBy(
                         adapter => new AdapterCounterKey(
                             Math.Max(0L, adapter.BytesReceived),
                             Math.Max(0L, adapter.BytesSent))))
            {
                bytesReceived += counterGroup.Key.BytesReceived;
                bytesSent += counterGroup.Key.BytesSent;
            }

            var adapterId = AdapterIdentity.BuildUsageGroupKey(adapterName, adapterType);
            normalizedAdapters[adapterId] = new AdapterUsageRecord
            {
                AdapterId = adapterId,
                AdapterName = adapterName,
                AdapterType = adapterType,
                BytesReceived = bytesReceived,
                BytesSent = bytesSent
            };
        }

        var normalizedBytesReceived = normalizedAdapters.Values.Sum(adapter => adapter.BytesReceived);
        var normalizedBytesSent = normalizedAdapters.Values.Sum(adapter => adapter.BytesSent);

        changed =
            normalizedBytesReceived != record.BytesReceived ||
            normalizedBytesSent != record.BytesSent ||
            normalizedAdapters.Count != record.Adapters.Count ||
            record.Adapters.Values.Any(adapter =>
                !string.Equals(
                    adapter.AdapterId,
                    AdapterIdentity.BuildUsageGroupKey(adapter.AdapterName, adapter.AdapterType),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    adapter.AdapterName,
                    AdapterIdentity.NormalizeDisplayName(adapter.AdapterName),
                    StringComparison.Ordinal));

        return new DailyUsageRecord
        {
            BytesReceived = normalizedBytesReceived,
            BytesSent = normalizedBytesSent,
            Adapters = normalizedAdapters
        };
    }

    private sealed class DailyUsageRecord
    {
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public Dictionary<string, AdapterUsageRecord> Adapters { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class AdapterUsageRecord
    {
        public string AdapterId { get; set; } = string.Empty;
        public string AdapterName { get; set; } = string.Empty;
        public string AdapterType { get; set; } = string.Empty;
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
    }

    private sealed class AdapterTotalsBuilder
    {
        public string AdapterId { get; set; } = string.Empty;
        public string AdapterName { get; set; } = string.Empty;
        public string AdapterType { get; set; } = string.Empty;
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
    }

    private readonly record struct AdapterCounterKey(long BytesReceived, long BytesSent);

    private sealed class UsageHistoryData
    {
        public int Version { get; set; }
        public List<DailyUsageData> Daily { get; set; } = [];
        public UsageCapData? Caps { get; set; }
        public AlertStateData? AlertState { get; set; }
    }

    private sealed class DailyUsageData
    {
        public string Date { get; set; } = string.Empty;
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public List<AdapterUsageData> Adapters { get; set; } = [];
    }

    private sealed class AdapterUsageData
    {
        public string AdapterId { get; set; } = string.Empty;
        public string AdapterName { get; set; } = string.Empty;
        public string AdapterType { get; set; } = string.Empty;
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
    }

    private sealed class UsageCapData
    {
        public long DailyCapBytes { get; set; }
        public long MonthlyCapBytes { get; set; }
    }

    private sealed class AlertStateData
    {
        public string LastDailyCapDate { get; set; } = string.Empty;
        public string LastMonthlyCapKey { get; set; } = string.Empty;
    }

    private sealed class LegacyUsageData
    {
        public string Date { get; set; } = string.Empty;
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
    }
}
