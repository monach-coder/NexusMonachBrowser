using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NexusMonach.Services;

public sealed record LocalPortInfo(string Protocol, string Address, int Port, int ProcessId, string ProcessName);

public static class WindowsPortService
{
    private const int AfInet = 2;
    public static IReadOnlyList<LocalPortInfo> GetListeningPorts()
    {
        var result = new List<LocalPortInfo>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                result.AddRange(ReadTcpListeners());
                result.AddRange(ReadUdpListeners());
            }
            catch { result.Clear(); }
        }

        try
        {
            // .NET добавляет IPv6 endpoints; для IPv4 приоритет останется у строк с PID из IP Helper.
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            result.AddRange(properties.GetActiveTcpListeners()
                .Select(x => new LocalPortInfo("TCP", x.Address.ToString(), x.Port, 0, "Процесс не определён")));
            result.AddRange(properties.GetActiveUdpListeners()
                .Select(x => new LocalPortInfo("UDP", x.Address.ToString(), x.Port, 0, "Процесс не определён")));
        }
        catch { /* Вызывающий код покажет «нет доступа». */ }

        return result.GroupBy(x => (x.Protocol, x.Address, x.Port))
            .Select(group => group.OrderByDescending(x => x.ProcessId != 0).First())
            .OrderBy(x => x.Protocol).ThenBy(x => x.Port).ThenBy(x => x.ProcessName).ToArray();
    }

    private static IEnumerable<LocalPortInfo> ReadTcpListeners()
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableClass.OwnerPidListener, 0);
        if (size <= 0) return [];
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableClass.OwnerPidListener, 0);
            if (status != 0) throw new InvalidOperationException("GetExtendedTcpTable: " + status);
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRow>();
            var rows = new List<LocalPortInfo>(count);
            var pointer = IntPtr.Add(buffer, sizeof(int));
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(IntPtr.Add(pointer, i * rowSize));
                rows.Add(ToInfo("TCP", row.LocalAddress, row.LocalPort, row.OwningPid));
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IEnumerable<LocalPortInfo> ReadUdpListeners()
    {
        var size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, true, AfInet, UdpTableClass.OwnerPid, 0);
        if (size <= 0) return [];
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = GetExtendedUdpTable(buffer, ref size, true, AfInet, UdpTableClass.OwnerPid, 0);
            if (status != 0) throw new InvalidOperationException("GetExtendedUdpTable: " + status);
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<UdpRow>();
            var rows = new List<LocalPortInfo>(count);
            var pointer = IntPtr.Add(buffer, sizeof(int));
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<UdpRow>(IntPtr.Add(pointer, i * rowSize));
                rows.Add(ToInfo("UDP", row.LocalAddress, row.LocalPort, row.OwningPid));
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static LocalPortInfo ToInfo(string protocol, uint address, uint rawPort, uint rawPid)
    {
        var pid = unchecked((int)rawPid);
        string process;
        try { process = Process.GetProcessById(pid).ProcessName; }
        catch { process = pid == 0 ? "System" : "PID " + pid; }
        return new LocalPortInfo(protocol, new IPAddress(address).ToString(), DecodePort(rawPort), pid, process);
    }

    private static int DecodePort(uint value) =>
        unchecked((ushort)IPAddress.NetworkToHostOrder((short)(value & 0xFFFF)));

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily,
        TcpTableClass tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int addressFamily,
        UdpTableClass tableClass, uint reserved);

    private enum TcpTableClass { OwnerPidListener = 3 }
    private enum UdpTableClass { OwnerPid = 1 }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UdpRow
    {
        public uint LocalAddress;
        public uint LocalPort;
        public uint OwningPid;
    }
}
