namespace NetUM;

internal static class AdapterIdentity
{
    private static readonly string[] FilterDriverMarkers =
    [
        "-QoS Packet Scheduler",
        "-WFP Native MAC Layer",
        "-WFP 802.3 MAC Layer",
        "-WEP Native MAC Layer",
        "-Npcap Packet Filter",
        "-Microsoft Network Adapter Multiplexor"
    ];

    public static string NormalizeDisplayName(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
            return string.Empty;

        foreach (var marker in FilterDriverMarkers)
        {
            var markerIndex = adapterName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex > 0)
                return adapterName[..markerIndex].TrimEnd(' ', '-');
        }

        return adapterName.Trim();
    }

    public static string BuildUsageGroupKey(string adapterName, string adapterType)
    {
        var normalizedName = NormalizeDisplayName(adapterName);
        return $"{adapterType}|{normalizedName}";
    }
}