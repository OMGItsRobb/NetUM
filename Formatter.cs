namespace NetUM;

public static class Formatter
{
    public static string FormatSpeed(double mbps)
    {
        if (mbps >= 1.0)
            return $"{mbps:F2} MB/s";

        var kbps = mbps * 1024.0;
        if (kbps >= 0.1)
            return $"{kbps:F1} KB/s";

        return "0 KB/s";
    }

    /// <summary>Short form for the tray icon (fits in ~6 chars).</summary>
    public static string FormatSpeedShort(double mbps)
    {
        if (mbps >= 100) return $"{(int)mbps}M";
        if (mbps >= 10)  return $"{mbps:F1}M";
        if (mbps >= 1)   return $"{mbps:F2}M";

        var kbps = mbps * 1024.0;
        if (kbps >= 100) return $"{(int)kbps}K";
        if (kbps >= 10)  return $"{kbps:F1}K";
        if (kbps >= 1)   return $"{kbps:F2}K";
        return "0K";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824L) return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576L)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024L)         return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }
}
