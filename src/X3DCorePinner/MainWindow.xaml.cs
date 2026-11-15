// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using X3DCorePinner.Core;
using X3DCorePinner.Services;

namespace X3DCorePinner
{
    public partial class MainWindow : Window
    {
        private readonly App _app = App.Instance;
        private readonly DispatcherTimer _timer;
        private readonly ObservableCollection<CcdRow> _ccdRows = new();
        private CcdMonitor? _monitor;
        private bool _loadingSettings;
        private bool _closeHintShown;
        private int _tickCount;
        private bool _faBusy;

        public MainWindow()
        {
            InitializeComponent();

            SourceInitialized += (_, _) => NativeWindow.UseDarkTitleBar(this);
            Closing += MainWindow_Closing;
            IsVisibleChanged += (_, _) => { if (IsVisible) _timer?.Start(); else _timer?.Stop(); };
            Closed += (_, _) =>
            {
                _timer?.Stop();
                _monitor?.Dispose();
                Log.Written -= OnLogWritten;
            };

            // CPU / CCD rows
            foreach (var c in _app.Topology.Ccds) _ccdRows.Add(new CcdRow(c, FindBrush(c.IsVCache ? "AccentBrush" : "FreqBrush")));
            CcdList.ItemsSource = _ccdRows;
            _monitor = new CcdMonitor(_app.Topology);
            if (_monitor.Error != null) Log.Warn($"Live CCD monitor unavailable: {_monitor.Error}");

            HeaderCpu.Text = _app.Topology.CpuName;
            CpuName.Text = _app.Topology.CpuName;
            CpuTopo.Text = _app.Topology.IsDualCcdX3D
                ? $"V-Cache CCD: CPUs {_app.Topology.VCacheCcd!.CpuRange} ({_app.Topology.VCacheCcd.L3SizeMB} MB L3)  |  " +
                  $"Frequency CCD: CPUs {_app.Topology.FrequencyCcd!.CpuRange} ({_app.Topology.FrequencyCcd.L3SizeMB} MB L3)"
                : _app.Topology.Summary;

            // Logs
            TxtLog.Text = string.Join(Environment.NewLine, Log.GetRecent().Select(e => e.ToString()));
            TxtLog.ScrollToEnd();
            Log.Written += OnLogWritten;

            // About
            AboutVersion.Text = $"Version {AppInfo.Version}  |  by {AppInfo.Author}  |  MIT License" +
                                (AppInfo.IsPortable ? "  |  portable mode" : "");
            RepoLink.NavigateUri = new Uri(AppInfo.RepoUrl);
            RepoLinkText.Text = AppInfo.RepoUrl.Replace("https://", "");

            LoadSettingsToUi();
            RefreshLists();
            RefreshAll();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => OnTick();
            _timer.Start();
        }

        // =================================================================== helpers

        private Brush FindBrush(string key) => (Brush)FindResource(key);

        private static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not open {dir}: {ex.Message}");
            }
        }

        private static List<string> RunningExeNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id <= 4) continue;
                        if (PinningEngine.ProtectedNames.Contains(p.ProcessName)) continue;
                        names.Add(p.ProcessName.ToLowerInvariant() + ".exe");
                    }
                    catch { }
                }
            }
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // =================================================================== refresh

        /// <summary>Re-render status, buttons and session info from the engine state.</summary>
        public void RefreshAll()
        {
            var topo = _app.Topology;
            var state = _app.Engine.State;
            string freq = topo.FrequencyCcd?.CpuRange ?? "-";
            string vc = topo.VCacheCcd?.CpuRange ?? "-";

            switch (state)
            {
                case EngineState.Protecting:
                    StatusTitle.Text = $"Protecting {_app.Engine.CurrentGame}";
                    StatusDetail.Text = $"Background apps are running on CPUs {freq} (frequency CCD). The V-Cache CCD (CPUs {vc}) is left to the game.";
                    StatusPillText.Text = "Protecting";
                    StatusDot.Fill = FindBrush("OkBrush");
                    break;
                case EngineState.Watching:
                    StatusTitle.Text = "Watching for games";
                    StatusDetail.Text = $"{_app.Config.Games.Count} games in your list. Protection starts automatically when one launches and stops when it closes.";
                    StatusPillText.Text = "Watching";
                    StatusDot.Fill = FindBrush("AccentBrush");
                    break;
                case EngineState.Paused:
                    StatusTitle.Text = "Protection paused";
                    StatusDetail.Text = "Background apps are not being moved. Resume to protect your games again.";
                    StatusPillText.Text = "Paused";
                    StatusDot.Fill = FindBrush("WarnBrush");
                    break;
                default:
                    StatusTitle.Text = "This CPU is not a dual-CCD X3D";
                    StatusDetail.Text = topo.Summary + " Diagnostics and frame analysis still work.";
                    StatusPillText.Text = "Not supported";
                    StatusDot.Fill = FindBrush("SubTextBrush");
                    break;
            }

            bool supported = state != EngineState.Unsupported;
            BtnToggle.IsEnabled = supported;
            BtnRestore.IsEnabled = supported;
            BtnToggle.Content = state == EngineState.Paused ? "Resume protection" : "Pause protection";

            RefreshSession();
            RefreshPinned();
        }

        private void RefreshSession()
        {
            var game = _app.Engine.CurrentGame;
            if (game != null)
            {
                var dur = DateTime.Now - _app.Engine.SessionStart;
                SessGame.Text = game;
                SessTime.Text = dur.TotalHours >= 1 ? $"{(int)dur.TotalHours}h {dur.Minutes:00}m" : $"{dur.Minutes}m {dur.Seconds:00}s";
            }
            else
            {
                SessGame.Text = "-";
                SessTime.Text = "-";
            }
        }

        private void RefreshPinned()
        {
            var rows = _app.Engine.GetPinned().Select(p => new PinnedRow(p)).ToList();
            SessPinned.Text = rows.Count.ToString();
            GridPinned.ItemsSource = rows;
        }

        private void RefreshLists()
        {
            LstGames.ItemsSource = _app.Config.Games.ToList();
            LstApps.ItemsSource = _app.Config.BackgroundApps.ToList();
        }

        private void RefreshRunning()
        {
            var running = RunningExeNames();
            LstRunningGames.ItemsSource = running.Where(n => !_app.Config.Games.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            LstRunningApps.ItemsSource = running.Where(n => !_app.Config.BackgroundApps.Contains(n, StringComparer.OrdinalIgnoreCase)
                                                        && !_app.Config.Games.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        private void OnTick()
        {
            _tickCount++;
            var sample = _monitor?.Sample();
            if (sample != null)
            {
                foreach (var row in _ccdRows)
                {
                    var load = sample.Ccds.FirstOrDefault(l => l.Ccd.Index == row.Index);
                    if (load != null) row.Update(load.AvgUtilPct, load.ParkedPct);
                }
            }
            RefreshSession();
            if (_tickCount % 3 == 0) RefreshPinned();
        }

        // =================================================================== settings

        private void LoadSettingsToUi()
        {
            _loadingSettings = true;
            try
            {
                ChkNotify.IsChecked = _app.Config.Notifications;
                ChkLowerPrio.IsChecked = _app.Config.LowerPriority;
                ChkCloseToTray.IsChecked = _app.Config.CloseToTray;
                TxtInterval.Text = _app.Config.IntervalSeconds.ToString();
                bool installed = false;
                try { installed = StartupTask.IsInstalled(); } catch { }
                ChkStartup.IsChecked = installed;
            }
            finally
            {
                _loadingSettings = false;
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;
            _app.Config.Notifications = ChkNotify.IsChecked == true;
            _app.Config.LowerPriority = ChkLowerPrio.IsChecked == true;
            _app.Config.CloseToTray = ChkCloseToTray.IsChecked == true;
            _app.SaveConfig();
        }

        private void ChkStartup_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;
            bool want = ChkStartup.IsChecked == true;
            bool ok;
            string msg;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                ok = want ? StartupTask.Install(out msg) : StartupTask.Uninstall(out msg);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            if (!ok) MessageBox.Show(this, msg, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadSettingsToUi();
        }

        private void TxtInterval_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxtInterval.Text.Trim(), out int s))
            {
                _app.Config.IntervalSeconds = Math.Clamp(s, 2, 60);
                _app.SaveConfig();
            }
            TxtInterval.Text = _app.Config.IntervalSeconds.ToString();
        }

        // =================================================================== dashboard buttons

        private void BtnToggle_Click(object sender, RoutedEventArgs e)
        {
            Mouse.OverrideCursor = Cursors.Wait;
            try { _app.ToggleProtection(); }
            finally { Mouse.OverrideCursor = null; }
            RefreshAll();
        }

        private void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(this,
                "Set every listed background app back to all CPU cores now?\n\nUse this if apps were left pinned after a crash. If a game is running, protection re-applies on the next check unless you pause it first.",
                AppInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            int n = _app.Engine.RestoreAllByName();
            MessageBox.Show(this, $"{n} process(es) set back to all cores.", AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshAll();
        }

        // =================================================================== games

        private void AddGames(IEnumerable<string> names)
        {
            int added = 0;
            foreach (var raw in names)
            {
                string n = AppConfig.NormalizeExe(raw);
                if (n.Length <= 4) continue;
                if (_app.Config.Games.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
                _app.Config.Games.Add(n);
                _app.Config.BackgroundApps.RemoveAll(a => a.Equals(n, StringComparison.OrdinalIgnoreCase));
                added++;
            }
            if (added == 0) return;
            _app.SaveConfig();
            RefreshLists();
            RefreshRunning();
            RefreshAll();
        }

        private void BtnAddGame_Click(object sender, RoutedEventArgs e)
        {
            AddGames(new[] { TxtGame.Text });
            TxtGame.Clear();
        }

        private void TxtGame_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnAddGame_Click(sender, e);
        }

        private void BtnRemoveGame_Click(object sender, RoutedEventArgs e)
        {
            var sel = LstGames.SelectedItems.Cast<string>().ToList();
            if (sel.Count == 0) return;
            _app.Config.Games.RemoveAll(g => sel.Contains(g, StringComparer.OrdinalIgnoreCase));
            _app.SaveConfig();
            RefreshLists();
            RefreshAll();
        }

        private void BtnResetGames_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Replace your game list with the default list?", AppInfo.ProductName,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _app.Config.Games = AppConfig.DefaultGames.ToList();
            _app.SaveConfig();
            RefreshLists();
            RefreshAll();
        }

        private void BtnAddRunningGame_Click(object sender, RoutedEventArgs e) =>
            AddGames(LstRunningGames.SelectedItems.Cast<string>().ToList());

        // =================================================================== background apps

        private void AddApps(IEnumerable<string> names)
        {
            int added = 0;
            var skipped = new List<string>();
            foreach (var raw in names)
            {
                string n = AppConfig.NormalizeExe(raw);
                if (n.Length <= 4) continue;
                string bare = Path.GetFileNameWithoutExtension(n);
                if (PinningEngine.ProtectedNames.Contains(bare) || _app.Config.Games.Contains(n, StringComparer.OrdinalIgnoreCase))
                {
                    skipped.Add(n);
                    continue;
                }
                if (_app.Config.BackgroundApps.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
                _app.Config.BackgroundApps.Add(n);
                added++;
            }
            if (skipped.Count > 0)
                MessageBox.Show(this,
                    "Skipped (system, security, anti-cheat or one of your games):\n\n" + string.Join("\n", skipped),
                    AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            if (added == 0) return;
            _app.SaveConfig();
            RefreshLists();
            RefreshRunning();
        }

        private void BtnAddApp_Click(object sender, RoutedEventArgs e)
        {
            AddApps(new[] { TxtApp.Text });
            TxtApp.Clear();
        }

        private void TxtApp_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnAddApp_Click(sender, e);
        }

        private void BtnRemoveApp_Click(object sender, RoutedEventArgs e)
        {
            var sel = LstApps.SelectedItems.Cast<string>().ToList();
            if (sel.Count == 0) return;
            _app.Config.BackgroundApps.RemoveAll(a => sel.Contains(a, StringComparer.OrdinalIgnoreCase));
            _app.SaveConfig();
            RefreshLists();
            RefreshRunning();
        }

        private void BtnResetApps_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Replace your background app list with the default list?", AppInfo.ProductName,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _app.Config.BackgroundApps = AppConfig.DefaultBackgroundApps.ToList();
            _app.SaveConfig();
            RefreshLists();
            RefreshRunning();
        }

        private void BtnAddRunningApp_Click(object sender, RoutedEventArgs e) =>
            AddApps(LstRunningApps.SelectedItems.Cast<string>().ToList());

        private void BtnRefreshRunning_Click(object sender, RoutedEventArgs e) => RefreshRunning();

        private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged bubbles up from inner ListBoxes/DataGrids - only react to the nav itself.
            if (!IsLoaded || !ReferenceEquals(e.OriginalSource, Nav)) return;
            if (Nav.SelectedIndex is 1 or 2 && LstRunningGames.Items.Count == 0) RefreshRunning();
            if (Nav.SelectedIndex == 4 && string.IsNullOrWhiteSpace(TxtFaGame.Text)) DetectGameForCapture();
        }

        // =================================================================== diagnostics

        private async void BtnAudit_Click(object sender, RoutedEventArgs e)
        {
            BtnAudit.IsEnabled = false;
            BtnAudit.Content = "Running...";
            try
            {
                var topo = _app.Topology;
                var items = await Task.Run(() => SystemAudit.Run(topo));
                AuditList.ItemsSource = items.Select(i => new AuditRow(i, FindBrush(i.Status switch
                {
                    AuditStatus.Ok => "OkBrush",
                    AuditStatus.Warn => "WarnBrush",
                    AuditStatus.Fail => "FailBrush",
                    _ => "InfoBrush"
                }))).ToList();
                Log.Info($"System audit: {items.Count(i => i.Status == AuditStatus.Fail)} fail, {items.Count(i => i.Status == AuditStatus.Warn)} warn.");
            }
            catch (Exception ex)
            {
                Log.Error($"Audit failed: {ex.Message}");
            }
            finally
            {
                BtnAudit.IsEnabled = true;
                BtnAudit.Content = "Run audit";
            }
        }

        private async void BtnTopCpu_Click(object sender, RoutedEventArgs e)
        {
            BtnTopCpu.IsEnabled = false;
            BtnTopCpu.Content = "Sampling...";
            try
            {
                GridTopCpu.ItemsSource = await ProcessCpuSampler.SampleAsync(_app.Config, TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Log.Error($"CPU sample failed: {ex.Message}");
            }
            finally
            {
                BtnTopCpu.IsEnabled = true;
                BtnTopCpu.Content = "Sample 5 seconds";
            }
        }

        // =================================================================== frame analysis

        private void DetectGameForCapture()
        {
            foreach (var g in _app.Config.Games)
            {
                var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(g));
                bool running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (running)
                {
                    TxtFaGame.Text = g;
                    return;
                }
            }
        }

        private void BtnFaDetect_Click(object sender, RoutedEventArgs e)
        {
            DetectGameForCapture();
            if (string.IsNullOrWhiteSpace(TxtFaGame.Text))
                FaProgress.Text = "None of your listed games is running. Start the game, or type its exe name.";
        }

        private async void BtnFaCapture_Click(object sender, RoutedEventArgs e)
        {
            if (_faBusy) return;
            string game = AppConfig.NormalizeExe(TxtFaGame.Text);
            if (game.Length <= 4)
            {
                FaProgress.Text = "Enter the game's exe name (for example cod.exe) or press Detect.";
                return;
            }
            if (!int.TryParse(TxtFaSeconds.Text.Trim(), out int seconds)) seconds = 60;
            seconds = Math.Clamp(seconds, 10, 600);
            TxtFaSeconds.Text = seconds.ToString();

            var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game));
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (!running)
            {
                FaProgress.Text = $"{game} is not running. Start the game and get into a match first.";
                return;
            }

            _faBusy = true;
            BtnFaCapture.IsEnabled = false;
            var progress = new Progress<string>(s => FaProgress.Text = s);
            try
            {
                string pm = await PresentMonService.EnsureAsync(progress, CancellationToken.None);
                string csv = await PresentMonService.CaptureAsync(pm, game, seconds, progress, CancellationToken.None);
                FaProgress.Text = "Analyzing...";
                var s = await Task.Run(() => PresentMonService.Analyze(csv));
                ShowFrameStats(s);
                FaProgress.Text = $"Done. {s.Frames:N0} frames analyzed. CSV saved to {csv}";
                Log.Ok($"Frame analysis {game}: avg {s.AvgFps} FPS, 1% low {s.OnePercentLowFps}, {s.Bottleneck}");
            }
            catch (Exception ex)
            {
                FaProgress.Text = "Frame analysis failed: " + ex.Message;
                Log.Error($"Frame analysis failed: {ex}");
            }
            finally
            {
                _faBusy = false;
                BtnFaCapture.IsEnabled = true;
            }
        }

        private void ShowFrameStats(FrameStats s)
        {
            FaResults.Visibility = Visibility.Visible;
            FaFps.Text = s.AvgFps.ToString("0.0");
            FaLow.Text = s.OnePercentLowFps.ToString("0.0");
            FaFrame.Text = $"{s.AvgFrameMs:0.00} ms";
            FaCpu.Text = s.AvgCpuBusyMs.HasValue ? $"{s.AvgCpuBusyMs:0.00} ms" : "n/a";
            FaGpu.Text = s.AvgGpuBusyMs.HasValue ? $"{s.AvgGpuBusyMs:0.00} ms" : "n/a";
            FaLat.Text = s.AvgLatencyMs.HasValue ? $"{s.AvgLatencyMs:0.0} ms" : "n/a";
            FaBottleneck.Text = s.Bottleneck;
            FaVerdict.Text = s.PresentVerdict;
            FaModes.Text = string.Join("   |   ", s.PresentModes.Select(m => $"{m.Mode}: {m.SharePct}%"));
        }

        private void BtnOpenReports_Click(object sender, RoutedEventArgs e) => OpenFolder(AppInfo.ReportsDir);

        // =================================================================== logs / about

        private void OnLogWritten(LogEntry entry)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (TxtLog.LineCount > 3000)
                    TxtLog.Text = string.Join(Environment.NewLine, Log.GetRecent().TakeLast(1500).Select(x => x.ToString()));
                else
                    TxtLog.AppendText((TxtLog.Text.Length > 0 ? Environment.NewLine : "") + entry);
                TxtLog.ScrollToEnd();
            });
        }

        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(AppInfo.LogDir);

        private void BtnOpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(AppInfo.ConfigDir);

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                // Through explorer.exe so the browser opens un-elevated in the user's session.
                Process.Start(new ProcessStartInfo("explorer.exe", e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not open link: {ex.Message}");
            }
            e.Handled = true;
        }

        // =================================================================== closing

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_app.IsExiting) return;

            e.Cancel = true;
            if (_app.Config.CloseToTray)
            {
                Hide();
                if (!_closeHintShown && _app.Config.Notifications)
                {
                    _closeHintShown = true;
                    _app.Tray.Notify("Still running in the tray",
                        "X3D Core Pinner keeps protecting your games. Right-click the tray icon to exit.");
                }
            }
            else
            {
                Dispatcher.InvokeAsync(_app.ExitApp);
            }
        }
    }

    // ======================================================================= view rows

    /// <summary>Live load row for one CCD on the dashboard.</summary>
    public sealed class CcdRow : INotifyPropertyChanged
    {
        private double _util;
        private string _valueText = "-";

        public CcdRow(CcdInfo ccd, Brush brush)
        {
            Index = ccd.Index;
            Label = $"{ccd.Label}  (CPUs {ccd.CpuRange}, {ccd.L3SizeMB} MB L3)";
            Brush = brush;
        }

        public int Index { get; }
        public string Label { get; }
        public Brush Brush { get; }

        public double Util
        {
            get => _util;
            private set { _util = value; OnChanged(nameof(Util)); }
        }

        public string ValueText
        {
            get => _valueText;
            private set { _valueText = value; OnChanged(nameof(ValueText)); }
        }

        public void Update(double util, double? parkedPct)
        {
            Util = Math.Clamp(util, 0, 100);
            ValueText = parkedPct.HasValue
                ? $"{util:0}% load  |  {parkedPct:0}% parked"
                : $"{util:0}% load";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class PinnedRow
    {
        public PinnedRow(PinnedProcess p)
        {
            Name = p.Name;
            Pid = p.Pid;
            OriginalMaskHex = $"0x{p.OriginalMask:X}";
            PriorityLowered = p.PriorityLowered ? "Yes" : "No";
            PinnedAtText = p.PinnedAt.ToString("HH:mm:ss");
        }

        public string Name { get; }
        public int Pid { get; }
        public string OriginalMaskHex { get; }
        public string PriorityLowered { get; }
        public string PinnedAtText { get; }
    }

    public sealed class AuditRow
    {
        public AuditRow(AuditItem item, Brush brush)
        {
            Name = item.Name;
            Detail = item.Detail;
            Fix = item.Fix;
            Brush = brush;
            StatusText = item.Status.ToString().ToUpperInvariant();
            FixVisibility = string.IsNullOrWhiteSpace(item.Fix) ? Visibility.Collapsed : Visibility.Visible;
        }

        public string Name { get; }
        public string Detail { get; }
        public string Fix { get; }
        public Brush Brush { get; }
        public string StatusText { get; }
        public Visibility FixVisibility { get; }
    }
}
