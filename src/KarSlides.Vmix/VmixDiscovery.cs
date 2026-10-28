using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace KarSlides.Vmix;

/// <summary>
/// Finds the ports vMix listens on (Windows TCP table with owning PIDs), so the Web Controller port does not
/// have to be configured: the candidates are probed for the vMix XML answer.
/// </summary>
public static class VmixDiscovery
{
    /// <summary>
    /// TCP ports in LISTEN state owned by vMix processes, the default 8088 first. Excludes the TCP API port and
    /// NDI ports (5960+), which do not speak HTTP. Empty if vMix is not running.
    /// </summary>
    public static IReadOnlyList<int> ListeningHttpCandidates(int tcpApiPort)
    {
        var pids = Process.GetProcessesByName("vMix64").Concat(Process.GetProcessesByName("vMix"))
            .Select(p => { using (p) return (uint)p.Id; })
            .ToHashSet();
        if (pids.Count == 0) return [];

        return ListeningPorts(pids)
            .Where(p => p != tcpApiPort && p is not (>= 5960 and < 6000))
            .Distinct()
            .OrderBy(p => p == 8088 ? 0 : 1).ThenBy(p => p)
            .ToArray();
    }

    private static IEnumerable<int> ListeningPorts(HashSet<uint> pids)
    {
        foreach (var family in new[] { AF_INET, AF_INET6 })
        {
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) continue;
                var count = Marshal.ReadInt32(buffer);
                var rowSize = family == AF_INET ? 24 : 56; // MIB_TCPROW_OWNER_PID / MIB_TCP6ROW_OWNER_PID
                var portOffset = family == AF_INET ? 8 : 20;
                var pidOffset = family == AF_INET ? 20 : 52;
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * rowSize;
                    var pid = (uint)Marshal.ReadInt32(row + pidOffset);
                    if (!pids.Contains(pid)) continue;
                    var raw = (ushort)Marshal.ReadInt32(row + portOffset); // network byte order in the low word
                    yield return (ushort)IPAddress.NetworkToHostOrder((short)raw);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_LISTENER = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
