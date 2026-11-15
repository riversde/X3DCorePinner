// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using X3DCorePinner.Core;
using X3DCorePinner.Services;

namespace X3DCorePinner
{
    public partial class App : Application
    {
        private const string MutexName = @"Global\PRFRAG.X3DCorePinner.SingleInstance";
        private const string ShowEventName = @"Global\PRFRAG.X3DCorePinner.Show";

        private Mutex? _mutex;
        private EventWaitHandle? _showEvent;
        private Thread? _showListener;
        private global::X3DCorePinner.MainWindow? _window;
        private bool _exiting;

        public static App Instance => (App)Current;

        public AppConfig Config { get; private set; } = AppConfig.CreateDefault();
        public CpuTopology Topology { get; private set; } = new CpuTopology();
        public PinningEngine Engine { get; private set; } = null!;
        public TrayService Tray { get; private set; } = null!;
        public bool IsExiting => _exiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ---- single instance: a second launch just brings the first window up
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                    ev.Set();
                }
                catch { }
                _mutex.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showListener = new Thread(ShowListenerLoop) { IsBackground = true, Name = "ShowListener" };
            _showListener.Start();

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Error($"Unhandled exception: {args.ExceptionObject}");

            // ---- core
            Log.Directory = AppInfo.LogDir;
            Log.Info($"{AppInfo.ProductName} {AppInfo.Version} starting{(AppInfo.IsPortable ? " (portable mode)" : "")}.");
            Config = AppConfig.Load();
            Log.Prune(Config.LogRetentionDays);

            Topology = CpuTopology.Detect();
            if (Topology.DetectionError != null) Log.Error($"CPU topology detection failed: {Topology.DetectionError}");
            Log.Info(Topology.Summary);

            Engine = new PinningEngine(Topology, Config.Clone());
            Engine.SessionStarted += s => Dispatcher.InvokeAsync(() => OnSessionStarted(s));
            Engine.SessionEnded += s => Dispatcher.InvokeAsync(() => OnSessionEnded(s));
            Engine.StateChanged += st => Dispatcher.InvokeAsync(() => OnEngineStateChanged(st));

            // ---- tray
            Tray = new TrayService();
            Tray.OpenRequested += ShowMainWindow;
            Tray.ToggleRequested += ToggleProtection;
            Tray.RestoreRequested += () => Engine.RestoreAllByName();
            Tray.ExitRequested += ExitApp;

            // ---- autostart task follows the exe if the portable folder was moved
            try
            {
                if (StartupTask.NeedsRepair() && StartupTask.Install(out _))
                    Log.Info("Start-with-Windows task updated to the new app location.");
            }
            catch (Exception ex) { Log.Warn($"Startup task check failed: {ex.Message}"); }

            if (Config.EngineEnabled) Engine.Start();
            OnEngineStateChanged(Engine.State);

            bool startInTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            if (startInTray)
            {
                if (Config.Notifications && Engine.State != EngineState.Unsupported)
                    Tray.Notify("Protection active", $"Watching {Config.Games.Count} games. Background apps move to CPUs {Topology.FrequencyCcd?.CpuRange} while you play.");
            }
            else
            {
                ShowMainWindow();
            }
        }

        private void ShowListenerLoop()
        {
            while (_showEvent != null)
            {
                try
                {
                    if (_showEvent.WaitOne()) Dispatcher.InvokeAsync(ShowMainWindow);
                }
                catch
                {
                    return; // handle closed during exit
                }
            }
        }

        public void ShowMainWindow()
        {
            if (_exiting) return;
            if (_window == null)
            {
                _window = new global::X3DCorePinner.MainWindow();
                _window.Closed += (_, _) => _window = null;
            }
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Topmost = true;   // bring to front reliably
            _window.Topmost = false;
            _window.Focus();
        }

        public void ToggleProtection()
        {
            if (Engine.State == EngineState.Unsupported) return;
            if (Engine.IsRunning)
            {
                Engine.Stop();
                Config.EngineEnabled = false;
            }
            else
            {
                Engine.Start();
                Config.EngineEnabled = true;
            }
            Config.Save();
            OnEngineStateChanged(Engine.State);
        }

        public void SaveConfig()
        {
            Config.Save();
            Engine.UpdateConfig(Config.Clone());
        }

        private void OnSessionStarted(SessionInfo s)
        {
            if (Config.Notifications)
                Tray.Notify("Game cores protected",
                    $"{s.Game} detected. {s.ProcessesPinned} background processes moved to CPUs {Topology.FrequencyCcd?.CpuRange}.");
            _window?.RefreshAll();
        }

        private void OnSessionEnded(SessionInfo s)
        {
            if (Config.Notifications)
                Tray.Notify("Game session ended",
                    $"{s.Game} closed after {Math.Round(s.Duration.TotalMinutes)} min. {s.ProcessesRestored} processes restored to all cores.");
            _window?.RefreshAll();
        }

        private void OnEngineStateChanged(EngineState st)
        {
            string text = st switch
            {
                EngineState.Protecting => $"{AppInfo.ProductName} - protecting {Engine.CurrentGame}",
                EngineState.Watching => $"{AppInfo.ProductName} - watching for games",
                EngineState.Paused => $"{AppInfo.ProductName} - paused",
                _ => $"{AppInfo.ProductName} - CPU not supported"
            };
            Tray.SetStatus(text, paused: st == EngineState.Paused, supported: st != EngineState.Unsupported);
            _window?.RefreshAll();
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error($"UI error: {e.Exception}");
            MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails were written to the log.",
                AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        public void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            Log.Info("Exiting - restoring all pinned processes.");
            try { Engine?.Stop(); } catch (Exception ex) { Log.Error($"Stop failed: {ex.Message}"); }
            try { _window?.Close(); } catch { }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { Engine?.Stop(); } catch { }
            try { Tray?.Dispose(); } catch { }
            var ev = _showEvent;
            _showEvent = null;
            try { ev?.Set(); ev?.Dispose(); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            base.OnExit(e);
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            // Windows sign-out / shutdown: restore affinities before processes are killed.
            try { Engine?.Stop(); } catch { }
            base.OnSessionEnding(e);
        }
    }
}
