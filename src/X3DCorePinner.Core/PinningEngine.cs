// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System.Diagnostics;

namespace X3DCorePinner.Core;

public enum EngineState
{
    /// <summary>User paused protection.</summary>
    Paused,
    /// <summary>Running, no configured game detected.</summary>
    Watching,
    /// <summary>A game is running and background apps are pinned.</summary>
    Protecting,
    /// <summary>CPU is not a dual-CCD X3D part; engine will not pin.</summary>
    Unsupported
}

public sealed record SessionInfo(string Game, DateTime Started, int ProcessesPinned, TimeSpan Duration, int ProcessesRestored);

public sealed record PinnedProcess(string Name, int Pid, ulong OriginalMask, bool PriorityLowered, DateTime PinnedAt);

/// <summary>
/// Watches for configured games. While one runs, every configured background app is
/// restricted to the Frequency CCD so the V-Cache CCD is left to the game. The game
/// process itself, anti-cheat and core OS processes are never touched. Everything is
/// restored to its original affinity when the game exits, the engine is paused, or
/// the app closes.
/// </summary>
public sealed class PinningEngine : IDisposable
{
    /// <summary>Never modified, even if a user adds them to the list.</summary>
    public static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "audiodg", "fontdrvhost", "sihost", "ctfmon", "memory compression",
        "memcompression", "msmpeng", "nissrv", "securityhealthservice", "explorer", "taskmgr",
        // Anti-cheat - never touch
        "easyanticheat", "easyanticheat_eos", "beservice", "beservice_x64", "battleye",
        "vgc", "vgtray", "faceit", "faceitservice", "faceitclient", "eaanticheat.gameservice",
        "atvi-randgrid_sc", "randgrid", "mhyprot", "xigncode", "nprotect", "gameguard",
        // Ourselves
        "x3dcorepinner"
    };

    private readonly CpuTopology _topology;
    private readonly object _gate = new();
    private readonly Dictionary<string, PinnedProcess> _pinned = new();
    private readonly HashSet<string> _deniedKeys = new();
    private readonly HashSet<string> _deniedNamesLogged = new(StringComparer.OrdinalIgnoreCase);

    private AppConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private EngineState _state;
    private string? _currentGame;
    private DateTime _sessionStart;
    private int _sessionPinnedTotal;

    public event Action<EngineState>? StateChanged;
    public event Action<SessionInfo>? SessionStarted;
    public event Action<SessionInfo>? SessionEnded;

    public PinningEngine(CpuTopology topology, AppConfig config)
    {
        _topology = topology;
        _config = config;
        _state = topology.IsDualCcdX3D ? EngineState.Paused : EngineState.Unsupported;
    }

    public EngineState State
    {
        get { lock (_gate) return _state; }
    }

    public string? CurrentGame
    {
        get { lock (_gate) return _currentGame; }
    }

    public DateTime SessionStart
    {
        get { lock (_gate) return _sessionStart; }
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public ulong TargetMask => _topology.FrequencyCcd?.Mask ?? 0;

    public IReadOnlyList<PinnedProcess> GetPinned()
    {
        lock (_gate) return _pinned.Values.OrderBy(p => p.Name).ThenBy(p => p.Pid).ToList();
    }

    public void UpdateConfig(AppConfig config)
    {
        lock (_gate) _config = config;
    }

    // ---------------------------------------------------------------- lifecycle

    public void Start()
    {
        if (!_topology.IsDualCcdX3D)
        {
            SetState(EngineState.Unsupported);
            Log.Warn("Engine not started: " + _topology.Summary);
            return;
        }
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetState(EngineState.Watching);
        Log.Ok($"Protection enabled. V-Cache CCD CPUs {_topology.VCacheCcd!.CpuRange}, background apps go to CPUs {_topology.FrequencyCcd!.CpuRange}.");
        _loop = Task.Run(() => LoopAsync(token));
    }

    /// <summary>Stop watching and restore every pinned process. Blocks until done.</summary>
    public void Stop()
    {
        if (_cts == null) return;
        try
        {
            _cts.Cancel();
            _loop?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException) { }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
        if (_topology.IsDualCcdX3D) SetState(EngineState.Paused);
        Log.Info("Protection paused.");
    }

    public void Dispose() => Stop();

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Tick(token);
                }
                catch (Exception ex)
                {
                    Log.Error($"Engine tick failed: {ex.Message}");
                }

                int interval;
                lock (_gate) interval = _config.IntervalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(interval), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            EndSessionIfActive("protection paused");
        }
    }

    private void Tick(CancellationToken token)
    {
        string? game = FindRunningGame();
        string? active;
        lock (_gate) active = _currentGame;

        if (game != null && active == null)
        {
            // New session: give the game a moment to spawn, then pin.
            Log.Ok($"{game} detected.");
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
            if (token.IsCancellationRequested) return;

            lock (_gate)
            {
                _currentGame = game;
                _sessionStart = DateTime.Now;
                _sessionPinnedTotal = 0;
            }
            int pinned = PinPass();
            lock (_gate) _sessionPinnedTotal = pinned;
            SetState(EngineState.Protecting);
            SessionStarted?.Invoke(new SessionInfo(game, DateTime.Now, pinned, TimeSpan.Zero, 0));
        }
        else if (game != null)
        {
            int added = PinPass();
            if (added > 0) lock (_gate) _sessionPinnedTotal += added;
        }
        else if (active != null)
        {
            EndSessionIfActive("game exited");
        }
    }

    private void EndSessionIfActive(string reason)
    {
        string? game;
        DateTime start;
        int total;
        lock (_gate)
        {
            game = _currentGame;
            start = _sessionStart;
            total = _sessionPinnedTotal;
            _currentGame = null;
        }
        int restored = RestoreAll();
        if (game != null)
        {
            var dur = DateTime.Now - start;
            Log.Ok($"{game} session ended ({reason}) after {dur.TotalMinutes:0} min. Restored {restored} process(es).");
            if (_topology.IsDualCcdX3D && _cts is { IsCancellationRequested: false })
                SetState(EngineState.Watching);
            SessionEnded?.Invoke(new SessionInfo(game, start, total, dur, restored));
        }
    }

    // ---------------------------------------------------------------- detection

    private string? FindRunningGame()
    {
        List<string> games;
        lock (_gate) games = _config.Games.ToList();
        foreach (var exe in games)
        {
            string name = Path.GetFileNameWithoutExtension(exe);
            Process[] procs = Array.Empty<Process>();
            try
            {
                procs = Process.GetProcessesByName(name);
                if (procs.Length > 0) return exe;
            }
            catch { }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- pinning

    private static string KeyOf(Process p)
    {
        try { return $"{p.Id}:{p.StartTime.Ticks}"; }
        catch { return $"{p.Id}:0"; }
    }

    /// <summary>Pin every not-yet-pinned target process. Returns how many were newly pinned.</summary>
    private int PinPass()
    {
        ulong target = TargetMask;
        if (target == 0) return 0;

        List<string> apps;
        HashSet<string> games;
        bool lowerPriority;
        lock (_gate)
        {
            apps = _config.BackgroundApps.ToList();
            games = new HashSet<string>(_config.Games.Select(Path.GetFileNameWithoutExtension)!, StringComparer.OrdinalIgnoreCase);
            lowerPriority = _config.LowerPriority;
        }

        int newlyPinned = 0;
        var liveKeys = new HashSet<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var exe in apps)
        {
            string name = Path.GetFileNameWithoutExtension(exe);
            if (ProtectedNames.Contains(name) || games.Contains(name)) continue;

            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                using (p)
                {
                    string key = KeyOf(p);
                    liveKeys.Add(key);
                    lock (_gate)
                    {
                        if (_pinned.ContainsKey(key) || _deniedKeys.Contains(key)) continue;
                    }

                    try
                    {
                        ulong original = (ulong)p.ProcessorAffinity.ToInt64();
                        p.ProcessorAffinity = new IntPtr(unchecked((long)target));

                        bool lowered = false;
                        if (lowerPriority)
                        {
                            try
                            {
                                if (p.PriorityClass == ProcessPriorityClass.Normal)
                                {
                                    p.PriorityClass = ProcessPriorityClass.BelowNormal;
                                    lowered = true;
                                }
                            }
                            catch { }
                        }

                        lock (_gate) _pinned[key] = new PinnedProcess(p.ProcessName, p.Id, original, lowered, DateTime.Now);
                        newlyPinned++;
                        counts[p.ProcessName] = counts.TryGetValue(p.ProcessName, out int c) ? c + 1 : 1;
                    }
                    catch (Exception ex)
                    {
                        lock (_gate)
                        {
                            _deniedKeys.Add(key);
                            if (_deniedNamesLogged.Add(p.ProcessName))
                                Log.Warn($"Skipped {p.ProcessName} (PID {p.Id}): {ex.Message}");
                        }
                    }
                }
            }
        }

        // Forget processes that have exited.
        lock (_gate)
        {
            foreach (var dead in _pinned.Keys.Where(k => !liveKeys.Contains(k)).ToList()) _pinned.Remove(dead);
            _deniedKeys.IntersectWith(liveKeys);
        }

        if (newlyPinned > 0)
            Log.Ok("Pinned to Frequency CCD: " + string.Join(", ", counts.Select(kv => $"{kv.Key} x{kv.Value}")));

        return newlyPinned;
    }

    /// <summary>Restore every process this engine pinned to its original affinity/priority.</summary>
    public int RestoreAll()
    {
        List<KeyValuePair<string, PinnedProcess>> snapshot;
        lock (_gate)
        {
            snapshot = _pinned.ToList();
            _pinned.Clear();
            _deniedKeys.Clear();
            _deniedNamesLogged.Clear();
        }

        int restored = 0;
        ulong all = _topology.AllCoresMask;
        foreach (var (key, info) in snapshot)
        {
            try
            {
                using var p = Process.GetProcessById(info.Pid);
                if (KeyOf(p) != key) continue; // PID reused by another process
                ulong mask = info.OriginalMask != 0 ? info.OriginalMask : all;
                p.ProcessorAffinity = new IntPtr(unchecked((long)mask));
                if (info.PriorityLowered)
                {
                    try
                    {
                        if (p.PriorityClass == ProcessPriorityClass.BelowNormal)
                            p.PriorityClass = ProcessPriorityClass.Normal;
                    }
                    catch { }
                }
                restored++;
            }
            catch
            {
                // Process exited - nothing to restore.
            }
        }
        return restored;
    }

    /// <summary>
    /// Emergency restore: set every configured background app back to all cores, even if
    /// this engine did not pin it (e.g. after a crash or an older script version).
    /// </summary>
    public int RestoreAllByName()
    {
        RestoreAll();
        List<string> apps;
        lock (_gate) apps = _config.BackgroundApps.ToList();
        ulong all = _topology.AllCoresMask;
        int n = 0;
        foreach (var exe in apps)
        {
            string name = Path.GetFileNameWithoutExtension(exe);
            if (ProtectedNames.Contains(name)) continue;
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in procs)
            {
                using (p)
                {
                    try { p.ProcessorAffinity = new IntPtr(unchecked((long)all)); n++; } catch { }
                }
            }
        }
        Log.Ok($"Emergency restore: {n} process(es) set back to all cores.");
        return n;
    }

    private void SetState(EngineState s)
    {
        bool changed;
        lock (_gate)
        {
            changed = _state != s;
            _state = s;
        }
        if (changed) StateChanged?.Invoke(s);
    }
}
