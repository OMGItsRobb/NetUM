using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetUM;

public sealed class NetworkAdapterDelta
{
    public string AdapterId { get; init; } = string.Empty;
    public string AdapterName { get; init; } = string.Empty;
    public string AdapterType { get; init; } = string.Empty;
    public long BytesReceivedDelta { get; init; }
    public long BytesSentDelta { get; init; }
    public long TotalBytesDelta => BytesReceivedDelta + BytesSentDelta;
}

/// <summary>
/// Samples network I/O statistics from active routed interfaces
/// and computes download/upload speed in MB/s.
/// </summary>
public class NetworkMonitor
{
    private readonly Dictionary<string, InterfaceSnapshot> _lastInterfaceBytes;
    private DateTime _lastSampleTime;

    public double DownloadSpeedMbps { get; private set; }
    public double UploadSpeedMbps { get; private set; }

    /// <summary>Bytes transferred since the previous Update() call.</summary>
    public long BytesReceivedDelta { get; private set; }
    public long BytesSentDelta { get; private set; }
    public IReadOnlyList<NetworkAdapterDelta> AdapterDeltas { get; private set; } = [];

    public NetworkMonitor()
    {
        _lastInterfaceBytes = GetInterfaceBytes();
        _lastSampleTime = DateTime.UtcNow;
    }

    public void Update()
    {
        var currentInterfaceBytes = GetInterfaceBytes();
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSampleTime).TotalSeconds;

        if (elapsed > 0)
        {
            long deltaReceived = 0;
            long deltaSent = 0;
            var adapterDeltas = new List<NetworkAdapterDelta>();

            foreach (var (id, current) in currentInterfaceBytes)
            {
                if (!_lastInterfaceBytes.TryGetValue(id, out var previous))
                {
                    // New interface: establish baseline to avoid counting historical bytes.
                    continue;
                }

                var adapterDown = Math.Max(0L, current.BytesReceived - previous.BytesReceived);
                var adapterUp = Math.Max(0L, current.BytesSent - previous.BytesSent);

                deltaReceived += adapterDown;
                deltaSent += adapterUp;

                if (adapterDown > 0 || adapterUp > 0)
                {
                    adapterDeltas.Add(new NetworkAdapterDelta
                    {
                        AdapterId = current.AdapterId,
                        AdapterName = current.AdapterName,
                        AdapterType = current.AdapterType,
                        BytesReceivedDelta = adapterDown,
                        BytesSentDelta = adapterUp
                    });
                }
            }

            DownloadSpeedMbps = deltaReceived / elapsed / (1024.0 * 1024.0);
            UploadSpeedMbps = deltaSent / elapsed / (1024.0 * 1024.0);
            BytesReceivedDelta = deltaReceived;
            BytesSentDelta = deltaSent;
            AdapterDeltas = adapterDeltas;
        }

        _lastInterfaceBytes.Clear();
        foreach (var (id, value) in currentInterfaceBytes)
            _lastInterfaceBytes[id] = value;

        _lastSampleTime = now;
    }

    private static Dictionary<string, InterfaceSnapshot> GetInterfaceBytes()
    {
        var totals = new Dictionary<string, InterfaceSnapshot>(StringComparer.Ordinal);

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!ShouldTrackAdapter(ni))
                continue;

            try
            {
                var properties = ni.GetIPProperties();
                var stats = ni.GetIPStatistics();
                var adapterId = BuildCanonicalAdapterId(ni.NetworkInterfaceType, properties);
                var snapshot = new InterfaceSnapshot
                {
                    AdapterId = adapterId,
                    AdapterName = AdapterIdentity.NormalizeDisplayName(ni.Name),
                    AdapterType = ni.NetworkInterfaceType.ToString(),
                    BytesReceived = stats.BytesReceived,
                    BytesSent = stats.BytesSent
                };

                if (totals.TryGetValue(adapterId, out var existing))
                {
                    totals[adapterId] = ChoosePreferredSnapshot(existing, snapshot);
                    continue;
                }

                totals[adapterId] = snapshot;
            }
            catch
            {
                // Some virtual/VPN adapters throw; skip them.
            }
        }

        return totals;
    }

    private static string BuildCanonicalAdapterId(
        NetworkInterfaceType adapterType,
        IPInterfaceProperties properties)
    {
        var gateways = properties.GatewayAddresses
            .Select(gateway => gateway.Address)
            .Where(address => IsUsableGateway(address))
            .Select(address => address.ToString())
            .OrderBy(address => address, StringComparer.Ordinal);

        var addresses = properties.UnicastAddresses
            .Select(unicast => unicast.Address)
            .Where(address => IsUsableUnicastAddress(address))
            .Select(address => address.ToString())
            .OrderBy(address => address, StringComparer.Ordinal);

        return string.Join(
            "|",
            adapterType,
            string.Join(",", gateways),
            string.Join(",", addresses));
    }

    private static InterfaceSnapshot ChoosePreferredSnapshot(
        InterfaceSnapshot current,
        InterfaceSnapshot candidate)
    {
        var useCandidateName = candidate.AdapterName.Length < current.AdapterName.Length;

        return new InterfaceSnapshot
        {
            AdapterId = current.AdapterId,
            AdapterName = useCandidateName ? candidate.AdapterName : current.AdapterName,
            AdapterType = current.AdapterType,
            BytesReceived = Math.Max(current.BytesReceived, candidate.BytesReceived),
            BytesSent = Math.Max(current.BytesSent, candidate.BytesSent)
        };
    }

    private static bool ShouldTrackAdapter(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            return false;
        if (ni.OperationalStatus != OperationalStatus.Up)
            return false;
        if (IsVirtualBridgeAdapter(ni))
            return false;

        try
        {
            var properties = ni.GetIPProperties();
            return HasUsableGateway(properties) && HasUsableUnicastAddress(properties);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasUsableGateway(IPInterfaceProperties properties)
    {
        return properties.GatewayAddresses.Any(gateway => IsUsableGateway(gateway.Address));
    }

    private static bool HasUsableUnicastAddress(IPInterfaceProperties properties)
    {
        return properties.UnicastAddresses.Any(unicast => IsUsableUnicastAddress(unicast.Address));
    }

    private static bool IsUsableGateway(IPAddress? address)
    {
        if (address is null)
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.None);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6None);

        return false;
    }

    private static bool IsUsableUnicastAddress(IPAddress? address)
    {
        if (address is null)
            return false;
        if (IPAddress.IsLoopback(address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            return !(octets[0] == 169 && octets[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !address.IsIPv6LinkLocal;

        return false;
    }

    /// <summary>
    /// Returns true for virtual adapters that mirror/duplicate traffic from
    /// a physical NIC (e.g. Hyper-V virtual switches), which would cause
    /// the same bytes to be counted twice.
    /// </summary>
    private static bool IsVirtualBridgeAdapter(NetworkInterface ni)
    {
        var desc = ni.Description ?? string.Empty;
        var name = ni.Name ?? string.Empty;

        // Hyper-V virtual switches bridge the physical NIC and report the
        // same traffic again, effectively doubling all byte counts.
        if (desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
            return true;

        // Wi-Fi Direct is used for mobile hotspot / screen projection,
        // not regular internet traffic.
        if (desc.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase))
            return true;

        // Kernel debug adapter is not user traffic.
        if (desc.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase))
            return true;

        // Microsoft ISATAP and 6to4 transition adapters.
        if (desc.Contains("isatap", StringComparison.OrdinalIgnoreCase))
            return true;
        if (desc.Contains("6to4", StringComparison.OrdinalIgnoreCase))
            return true;
        if (desc.Contains("Teredo", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private sealed class InterfaceSnapshot
    {
        public string AdapterId { get; init; } = string.Empty;
        public string AdapterName { get; init; } = string.Empty;
        public string AdapterType { get; init; } = string.Empty;
        public long BytesReceived { get; init; }
        public long BytesSent { get; init; }
    }
}
