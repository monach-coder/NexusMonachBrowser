using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace NexusMonach.Services.Tor;

/// <summary>Запись ARP-таблицы Windows (MIB_IPNETROW).</summary>
internal readonly record struct ArpRow(
    int InterfaceIndex, uint Address, string Mac, uint Type);

/// <summary>
/// ARP-страж настоящего уровня. Читает НАСТОЯЩУЮ ARP-таблицу ядра Windows
/// через GetIpNetTable (P/Invoke) и сравнивает MAC шлюза по каждому
/// интерфейсу отдельно — ложные срабатывания от VPN- и виртуальных
/// адаптеров исключены: у каждого интерфейса своё состояние.
///
/// При подлинной подмене (MAC шлюза сменился у одного и того же
/// интерфейса) страж:
///   1. Фиксирует доказательства: старый и новый MAC, имя интерфейса и
///      полный снимок ARP-таблицы атакуемого интерфейса — видно, кто
///      ещё объявляется в сети.
///   2. Закрепляет статическую ARP-запись шлюза (исходный MAC) через
///      единый скрытый механизм PortShieldService — подмена перестаёт
///      действовать до перезагрузки.
///   3. Оповещает один раз на комбинацию (шлюз, старый→новый MAC),
///      а не спамит каждые пять секунд.
/// </summary>
internal static class ArpGuard
{
    private static readonly Dictionary<string, string> MacByGateway = new();
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static int _pinAttempted;

    /// <summary>
    /// Проверяет все активные IPv4-шлюзы. Возвращает угрозу при подлинной
    /// подмене (null в обычном случае). Каждый факт — ровно один раз.
    /// </summary>
    public static ThreatEvent? Check()
    {
        var rows = ReadArpTable();
        if (rows.Count == 0) return null;
        var byIndex = rows.GroupBy(r => r.InterfaceIndex)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var gateway = ni.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (gateway is null) continue;

            var ipv4 = ni.GetIPProperties().GetIPv4Properties();
            if (ipv4 is null) continue;
            if (!byIndex.TryGetValue(ipv4.Index, out var interfaceRows)) continue;
            var row = interfaceRows.FirstOrDefault(r =>
                r.Address == BitConverter.ToUInt32(gateway.GetAddressBytes(), 0) &&
                IsUsable(r));
            if (row.Mac is null) continue;

            var key = ipv4.Index + "|" + gateway;
            if (!MacByGateway.TryGetValue(key, out var baseline))
            {
                MacByGateway[key] = row.Mac;
                continue;
            }
            if (baseline == row.Mac) continue;

            // Подмена: новый MAC у того же интерфейса и шлюза.
            var reportKey = key + "|" + baseline + ">" + row.Mac;
            MacByGateway[key] = row.Mac;
            if (!Reported.Add(reportKey)) return null; // уже сообщили — не спамим

            var evidence = BuildEvidence(gateway.ToString(), ni.Name, baseline, row.Mac, interfaceRows);
            var pinned = TryPinGateway(ni.Name, gateway.ToString(), baseline);
            return new ThreatEvent(
                ThreatType.ArpSpoofing,
                gateway.ToString(),
                evidence,
                DateTimeOffset.Now,
                pinned
                    ? $"Статическая ARP-запись закреплена на исходный MAC {FormatMac(baseline)} — подмена нейтрализована до перезагрузки. Атакующий объявляет себя как {FormatMac(row.Mac)}."
                    : $"Закрепить статическую ARP-запись не удалось — проверьте сеть вручную. Атакующий MAC: {FormatMac(row.Mac)}.");
        }
        return null;
    }

    /// <summary>Доказательства: что изменилось и кто живёт в ARP-таблице.</summary>
    internal static string BuildEvidence(
        string gateway, string interfaceName, string baselineMac, string attackerMac,
        List<ArpRow> interfaceRows)
    {
        var evidence = new StringBuilder();
        evidence.Append($"Шлюз {gateway} ({interfaceName}): MAC изменён " +
                        $"{FormatMac(baselineMac)} → {FormatMac(attackerMac)}.");
        evidence.AppendLine();
        evidence.Append("Снимок ARP-таблицы интерфейса (кто объявляется в сети):");
        foreach (var r in interfaceRows.Where(IsUsable).Take(12))
            evidence.AppendLine().AppendFormat("  {0} → {1}", IntToIp(r.Address), FormatMac(r.Mac));
        return evidence.ToString();
    }

    private static bool IsUsable(ArpRow row) =>
        row.Type is 3 or 4 && row.Mac.Length > 0 && !row.Mac.StartsWith("00000000000");

    internal static string FormatMac(string raw) =>
        string.Join("-", Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2)));

    internal static string IntToIp(uint value) =>
        $"{value & 0xFF}.{(value >> 8) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 24) & 0xFF}";

    /// <summary>
    /// Закрепляет шлюз за исходным MAC: исполнение делегируется единому
    /// механизму PortShieldService.RunElevatedScript (conhost --headless).
    /// Статическая запись не перезаписывается ARP-ответами атакующего.
    /// </summary>
    private static bool TryPinGateway(string interfaceName, string gateway, string mac)
    {
        if (Interlocked.Exchange(ref _pinAttempted, 1) != 0) return false;
        try
        {
            return PortShieldService.RunElevatedScript(
                BuildPinScript(interfaceName, gateway, FormatMac(mac)), "nexus-arp-pin").Result;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Скрипт закрепления: идемпотентен, в аргументы процесса не попадает.</summary>
    internal static string BuildPinScript(string interfaceAlias, string gateway, string formattedMac) =>
        "$ErrorActionPreference = 'SilentlyContinue'\n" +
        $"Remove-NetNeighbor -InterfaceAlias '{interfaceAlias}' -IPAddress {gateway}\n" +
        $"New-NetNeighbor -InterfaceAlias '{interfaceAlias}' -IPAddress {gateway} " +
        $"-LinkLayerAddress '{formattedMac}' -State Permanent | Out-Null\n";

    #region GetIpNetTable (P/Invoke)

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetRow
    {
        public uint Index;
        public uint PhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] PhysAddr;
        public uint Addr;
        public uint Type;
    }

    /// <summary>Читает ARP-таблицу ядра: IP → MAC по каждому интерфейсу.</summary>
    internal static List<ArpRow> ReadArpTable()
    {
        var size = 0;
        _ = GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return [];
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0) return [];
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpNetRow>();
            var rows = new List<ArpRow>(count);
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibIpNetRow>(IntPtr.Add(buffer, sizeof(int) + i * rowSize));
                rows.Add(new ArpRow(
                    unchecked((int)row.Index),
                    row.Addr,
                    BitConverter.ToString(row.PhysAddr, 0, (int)Math.Min(row.PhysAddrLen, 6)).Replace("-", ""),
                    row.Type));
            }
            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpNetTable(IntPtr table, ref int size, bool order);

    #endregion
}
