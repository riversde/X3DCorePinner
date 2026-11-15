// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace X3DCorePinner.Core;

/// <summary>One L3 cache domain (= one CCD on Ryzen).</summary>
public sealed record CcdInfo(
    int Index,
    int L3SizeMB,
    ushort Group,
    ulong Mask,
    IReadOnlyList<int> LogicalCpus,
    bool IsVCache)
{
    public string CpuRange => LogicalCpus.Count == 0 ? "n/a" : $"{LogicalCpus[0]}-{LogicalCpus[^1]}";
    public string Label => IsVCache ? "V-Cache CCD" : "Frequency CCD";
}

/// <summary>
/// Reads the real L3 cache topology from Windows (GetLogicalProcessorInformationEx).
/// A dual-CCD X3D part (7950X3D, 7900X3D, 9950X3D, 9900X3D) shows two L3 domains of
/// different sizes: the larger one is the 3D V-Cache CCD.
/// </summary>
public sealed class CpuTopology
{
    public string CpuName { get; init; } = "Unknown CPU";
    public int LogicalProcessors { get; init; }
    public IReadOnlyList<CcdInfo> Ccds { get; init; } = Array.Empty<CcdInfo>();
    public string? DetectionError { get; init; }

    /// <summary>Two L3 domains with different sizes -> dual-CCD X3D.</summary>
    public bool IsDualCcdX3D =>
        Ccds.Count == 2 && Ccds[0].L3SizeMB != Ccds[1].L3SizeMB && Ccds.All(c => c.Group == 0);

    /// <summary>Single L3 domain of 96 MB or more (7800X3D, 9800X3D...).</summary>
    public bool IsSingleCcdX3D => Ccds.Count == 1 && Ccds[0].L3SizeMB >= 96;

    public CcdInfo? VCacheCcd => IsDualCcdX3D ? Ccds.First(c => c.IsVCache) : null;
    public CcdInfo? FrequencyCcd => IsDualCcdX3D ? Ccds.First(c => !c.IsVCache) : null;

    public ulong AllCoresMask => LogicalProcessors >= 64
        ? ulong.MaxValue
        : (1UL << LogicalProcessors) - 1UL;

    /// <summary>Map logical CPU index -> CCD (null if unknown).</summary>
    public CcdInfo? CcdOf(int logicalCpu) =>
        Ccds.FirstOrDefault(c => c.LogicalCpus.Contains(logicalCpu));

    public string Summary
    {
        get
        {
            if (IsDualCcdX3D)
                return $"{CpuName} - V-Cache CCD: CPUs {VCacheCcd!.CpuRange} ({VCacheCcd.L3SizeMB} MB L3), " +
                       $"Frequency CCD: CPUs {FrequencyCcd!.CpuRange} ({FrequencyCcd.L3SizeMB} MB L3)";
            if (IsSingleCcdX3D)
                return $"{CpuName} - single-CCD X3D ({Ccds[0].L3SizeMB} MB L3): every core already has V-Cache, pinning not needed.";
            return $"{CpuName} - {Ccds.Count} L3 domain(s): not a dual-CCD X3D CPU, pinning disabled.";
        }
    }

    // ---------------------------------------------------------------- detection

    public static CpuTopology Detect()
    {
        string name = ReadCpuName();
        int logical = Environment.ProcessorCount;
        try
        {
            var raw = ReadL3Domains();
            // Deduplicate identical masks and order by first logical CPU.
            var distinct = raw
                .GroupBy(r => (r.Group, r.Mask))
                .Select(g => g.First())
                .OrderBy(r => r.Group)
                .ThenBy(r => FirstBit(r.Mask))
                .ToList();

            int maxMB = distinct.Count == 0 ? 0 : distinct.Max(d => d.SizeMB);
            bool sizesDiffer = distinct.Select(d => d.SizeMB).Distinct().Count() > 1;

            var ccds = distinct.Select((d, i) => new CcdInfo(
                Index: i,
                L3SizeMB: d.SizeMB,
                Group: d.Group,
                Mask: d.Mask,
                LogicalCpus: BitsOf(d.Mask).Select(b => d.Group * 64 + b).ToList(),
                IsVCache: sizesDiffer && d.SizeMB == maxMB)).ToList();

            return new CpuTopology { CpuName = name, LogicalProcessors = logical, Ccds = ccds };
        }
        catch (Exception ex)
        {
            return new CpuTopology
            {
                CpuName = name,
                LogicalProcessors = logical,
                DetectionError = ex.Message
            };
        }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown CPU";
        }
        catch { return "Unknown CPU"; }
    }

    private static int FirstBit(ulong mask)
    {
        for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) return i;
        return 64;
    }

    private static IEnumerable<int> BitsOf(ulong mask)
    {
        for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) yield return i;
    }

    private readonly record struct L3Raw(int SizeMB, ushort Group, ulong Mask);

    private const int RelationCache = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    /// <summary>
    /// Walk SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX records (x64 layout):
    ///   0 Relationship(int) 4 Size(uint) 8 CACHE_RELATIONSHIP:
    ///   8 Level(byte) 9 Associativity 10 LineSize(ushort) 12 CacheSize(uint)
    ///   16 Type(int) 20 Reserved[18] 38 GroupCount(ushort) 40 GROUP_AFFINITY{Mask(8) Group(2)}
    /// </summary>
    private static List<L3Raw> ReadL3Domains()
    {
        var list = new List<L3Raw>();
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref len);
        if (len == 0) throw new Win32Exception(Marshal.GetLastWin32Error());

        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationCache, buf, ref len))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            long off = 0;
            while (off < len)
            {
                IntPtr p = IntPtr.Add(buf, (int)off);
                int rel = Marshal.ReadInt32(p, 0);
                int size = Marshal.ReadInt32(p, 4);
                if (rel == RelationCache && Marshal.ReadByte(p, 8) == 3)
                {
                    uint cacheBytes = (uint)Marshal.ReadInt32(p, 12);
                    ulong mask = (ulong)Marshal.ReadInt64(p, 40);
                    ushort group = (ushort)Marshal.ReadInt16(p, 48);
                    list.Add(new L3Raw((int)Math.Round(cacheBytes / 1048576.0), group, mask));
                }
                if (size <= 0) break;
                off += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return list;
    }
}
