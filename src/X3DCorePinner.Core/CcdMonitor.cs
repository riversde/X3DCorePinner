// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Runtime.InteropServices;

namespace X3DCorePinner.Core;

public sealed record CcdLoad(CcdInfo Ccd, double AvgUtilPct, double? ParkedPct);

public sealed record CcdSample(DateTime Time, IReadOnlyList<CcdLoad> Ccds, IReadOnlyDictionary<int, double> PerCpuUtil);

/// <summary>
/// Live per-CCD load and core parking via PDH (English counter names, so it works on
/// any Windows display language). No WMI, no extra packages.
/// </summary>
public sealed class CcdMonitor : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint ERROR_SUCCESS = 0;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private readonly CpuTopology _topo;
    private IntPtr _query;
    private IntPtr _utilCounter;
    private IntPtr _parkCounter;
    private bool _primed;

    public bool ParkingSupported => _parkCounter != IntPtr.Zero;
    public string? Error { get; private set; }

    public CcdMonitor(CpuTopology topo)
    {
        _topo = topo;
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != ERROR_SUCCESS) { Error = "PdhOpenQuery failed"; return; }
            if (PdhAddEnglishCounterW(_query, @"\Processor Information(*)\% Processor Time", IntPtr.Zero, out _utilCounter) != ERROR_SUCCESS)
            {
                Error = "Processor Information counters unavailable (try 'lodctr /r' as admin).";
                _utilCounter = IntPtr.Zero;
            }
            if (PdhAddEnglishCounterW(_query, @"\Processor Information(*)\Parking Status", IntPtr.Zero, out _parkCounter) != ERROR_SUCCESS)
                _parkCounter = IntPtr.Zero;
            PdhCollectQueryData(_query);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Take a sample. Call at an interval (e.g. 1 s); the first call primes rates and returns null.</summary>
    public CcdSample? Sample()
    {
        if (_query == IntPtr.Zero || _utilCounter == IntPtr.Zero) return null;
        if (PdhCollectQueryData(_query) != ERROR_SUCCESS) return null;
        if (!_primed) { _primed = true; return null; }

        var util = ReadArray(_utilCounter);
        var park = _parkCounter != IntPtr.Zero ? ReadArray(_parkCounter) : new Dictionary<int, double>();

        var loads = new List<CcdLoad>();
        foreach (var ccd in _topo.Ccds)
        {
            var u = ccd.LogicalCpus.Where(util.ContainsKey).Select(i => util[i]).ToList();
            var pk = ccd.LogicalCpus.Where(park.ContainsKey).Select(i => park[i]).ToList();
            loads.Add(new CcdLoad(
                ccd,
                u.Count > 0 ? u.Average() : 0,
                pk.Count > 0 ? 100.0 * pk.Count(v => v >= 1) / pk.Count : null));
        }
        return new CcdSample(DateTime.Now, loads, util);
    }

    /// <summary>Read "group,cpu" instances into logicalIndex -> value.</summary>
    private static Dictionary<int, double> ReadArray(IntPtr counter)
    {
        var result = new Dictionary<int, double>();
        uint size = 0;
        uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return result;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf);
            if (status != ERROR_SUCCESS) return result;

            // PDH_FMT_COUNTERVALUE_ITEM_W (x64): szName ptr (8) | CStatus uint (4) | pad (4) | double (8) = 24 bytes
            int itemSize = IntPtr.Size + 16;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = IntPtr.Add(buf, i * itemSize);
                string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0));
                if (name == null) continue;
                int comma = name.IndexOf(',');
                if (comma <= 0 || name.Contains('_')) continue; // skip _Total / 0,_Total
                if (!int.TryParse(name.AsSpan(0, comma), out int group)) continue;
                if (!int.TryParse(name.AsSpan(comma + 1), out int cpu)) continue;
                uint cstatus = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                if (cstatus > 1) continue; // PDH_CSTATUS_VALID_DATA = 0, NEW_DATA = 1
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                result[group * 64 + cpu] = value;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}
