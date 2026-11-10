using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using CS2Manager.Models;
using CS2Manager.Services;
using Application = System.Windows.Application;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace CS2Manager;

public partial class MainWindow : Window
{
    private readonly Cs2ManagerService _service = new();
    private readonly StandbyListCleaner _cleaner = new();
    private AppSettings _settings = AppSettings.Load();
    private RunState _state = new();
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private Process? _cs2;                 // Prozess des laufenden Start-Ablaufs
    private Process? _monitorProcess;      // nur für die Anzeige
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _cleanerCts;
    private Task? _cleanerTask;
    private bool _busy;
    private bool _closeRequested;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSample;
    private Forms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"CS2 MANAGER v{AppInfo.Version}";
        VersionText.Text = $"VERSION {AppInfo.Version}  /  PERFORMANCE CONTROL CENTER";
        InitializeTrayIcon();

        _service.Log += message => Dispatcher.BeginInvoke(new Action(() => AddLog(message)));
        _cleaner.Log += message => Dispatcher.BeginInvoke(new Action(() => AddLog(message)));

        LoadSettingsToUi();
        _monitorTimer.Tick += (_, _) => UpdateMetrics();
        StateChanged += (_, _) => UpdateMonitorTimer();
        UpdateMonitorTimer();

        AddLog($"CS2 Manager v{AppInfo.Version} initialisiert.");
        AddLog("Einstellungen: " + AppSettings.SettingsPath);
        if (AppSettings.LoadNote != null) AddLog(AppSettings.LoadNote);

        Loaded += async (_, _) => await RecoverPendingStateAsync();
    }

    // ------------------------------------------------------------------ Tray

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("CS2 Manager öffnen", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => Close());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = $"CS2 Manager v{AppInfo.Version}",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/CS2Manager.ico"));
            if (info != null)
            {
                using Stream stream = info.Stream;
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch { }
        return Drawing.SystemIcons.Application;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void MinimizeAfterGameStart()
    {
        WindowState = WindowState.Minimized;
        AddLog("CS2 erkannt – Manager in die Taskleiste minimiert. Über das Infobereich-Symbol wieder öffnen.");
    }

    // ------------------------------------------------------------------ Einstellungen

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        int index = combo.Items.Cast<ComboBoxItem>().ToList()
            .FindIndex(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = index >= 0 ? index : 0;
    }

    private void LoadSettingsToUi()
    {
        CpuProfileBox.Text = _settings.CpuProfile;
        AffinityMaskBox.Text = _settings.AffinityMask.ToString(CultureInfo.InvariantCulture);
        TimeoutBox.Text = _settings.StartTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        LoadWaitBox.Text = _settings.LoadWaitSeconds.ToString(CultureInfo.InvariantCulture);
        SecondPassDelayBox.Text = _settings.SecondPassDelaySeconds.ToString(CultureInfo.InvariantCulture);
        PriorityCombo.SelectedIndex = _settings.Priority switch
        {
            "AboveNormal" => 1, "Normal" => 2, "BelowNormal" => 3, _ => 0
        };

        SelectComboByTag(CleanerModeCombo, _settings.CleanerMode);
        CleanerStandbyBox.Text = _settings.CleanerStandbyMb.ToString(CultureInfo.InvariantCulture);
        CleanerFreeBox.Text = _settings.CleanerFreeMb.ToString(CultureInfo.InvariantCulture);
        CleanerIntervalBox.Text = _settings.CleanerIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        EnableTimerResolutionCheck.IsChecked = _settings.EnableTimerResolution;
        SelectComboByTag(TimerResolutionCombo, _settings.TimerResolution100Ns.ToString(CultureInfo.InvariantCulture));

        EnableMonitoringCheck.IsChecked = _settings.EnableMonitoring;
        SetAffinityCheck.IsChecked = _settings.SetAffinity;
        SetPriorityCheck.IsChecked = _settings.SetPriority;
        ConnectDelayBox.Text = _settings.ConnectDelaySeconds.ToString(CultureInfo.InvariantCulture);
        SelectComboByTag(TcpAutoTuningCombo, _settings.EnableTcpAutoTuning ? _settings.TcpAutoTuningLevel : "unchanged");
        DetectCpuInfo();
        StopOneDriveCheck.IsChecked = _settings.StopOneDrive;
        RestartOneDriveCheck.IsChecked = _settings.RestartOneDrive;
        StopExtraCheck.IsChecked = _settings.StopExtraProcesses;
        ExtraProcessesBox.Text = _settings.ExtraProcesses;
        RestartExtraCheck.IsChecked = _settings.RestartExtraProcesses;
        StopWindowsSearchCheck.IsChecked = _settings.StopWindowsSearch;
        SingleDisplayCheck.IsChecked = _settings.AskForSingleDisplay;
        RestoreDisplayCheck.IsChecked = _settings.RestoreDisplay;
    }

    private static bool TryReadInt(TextBox box, int min, int max, string label, out int value)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= min && value <= max)
            return true;

        MessageBox.Show($"{label} muss eine ganze Zahl zwischen {min} und {max} sein.",
            "Ungültige Eingabe", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static bool TryParseMask(string text, out int mask)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask) && mask > 0;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask) && mask > 0;
    }

    private bool ReadSettingsFromUi()
    {
        if (!TryParseMask(AffinityMaskBox.Text, out int mask))
        {
            MessageBox.Show("Bitte eine gültige positive Affinity-Maske eingeben (dezimal oder als 0x-Hexwert).",
                "Ungültige Affinity", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        int cpuCount = Environment.ProcessorCount;
        long validBits = cpuCount >= 31 ? int.MaxValue : (1L << cpuCount) - 1;
        if ((mask & validBits) == 0 || ((long)mask & ~validBits) != 0)
        {
            MessageBox.Show($"Die Affinity-Maske passt nicht zu diesem System: Windows meldet {cpuCount} logische Prozessoren " +
                            $"(gültig sind Bits bis 0x{validBits:X}).",
                "Ungültige Affinity", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!TryReadInt(TimeoutBox, 5, 900, "Der Start-Timeout", out int timeout)) return false;
        if (!TryReadInt(LoadWaitBox, 0, 300, "Die Wartezeit auf das Spielfenster", out int loadWait)) return false;
        if (!TryReadInt(SecondPassDelayBox, 0, 300, "Der Abstand zum zweiten Durchgang", out int secondPassDelay)) return false;
        if (!TryReadInt(CleanerStandbyBox, 64, 262144, "Die Standby-Schwelle (MB)", out int standbyMb)) return false;
        if (!TryReadInt(CleanerFreeBox, 64, 262144, "Die Schwelle für freien Speicher (MB)", out int freeMb)) return false;
        if (!TryReadInt(CleanerIntervalBox, 1, 120, "Das Prüfintervall (Sek.)", out int intervalSeconds)) return false;
        if (!TryReadInt(ConnectDelayBox, 0, 300, "Die Steam-Link-Verzögerung (Sek.)", out int connectDelay)) return false;

        _settings.CpuProfile = CpuProfileBox.Text.Trim();
        _settings.AffinityMask = mask;
        _settings.StartTimeoutSeconds = timeout;
        _settings.LoadWaitSeconds = loadWait;
        _settings.SecondPassDelaySeconds = secondPassDelay;
        _settings.Priority = (PriorityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "High";

        _settings.CleanerMode = (CleanerModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        _settings.CleanerStandbyMb = standbyMb;
        _settings.CleanerFreeMb = freeMb;
        _settings.CleanerIntervalSeconds = intervalSeconds;
        _settings.ConnectDelaySeconds = connectDelay;
        _settings.EnableTimerResolution = EnableTimerResolutionCheck.IsChecked == true;
        _settings.TimerResolution100Ns = int.TryParse((TimerResolutionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int timerRes)
            ? timerRes : 5000;

        _settings.EnableMonitoring = EnableMonitoringCheck.IsChecked == true;
        _settings.SetAffinity = SetAffinityCheck.IsChecked == true;
        _settings.SetPriority = SetPriorityCheck.IsChecked == true;
        _settings.TcpAutoTuningLevel = (TcpAutoTuningCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "disabled";
        _settings.EnableTcpAutoTuning = _settings.TcpAutoTuningLevel != "unchanged";
        _settings.StopOneDrive = StopOneDriveCheck.IsChecked == true;
        _settings.RestartOneDrive = RestartOneDriveCheck.IsChecked == true;
        _settings.StopExtraProcesses = StopExtraCheck.IsChecked == true;
        _settings.ExtraProcesses = ExtraProcessesBox.Text.Trim();
        _settings.RestartExtraProcesses = RestartExtraCheck.IsChecked == true;
        _settings.StopWindowsSearch = StopWindowsSearchCheck.IsChecked == true;
        _settings.AskForSingleDisplay = SingleDisplayCheck.IsChecked == true;
        _settings.RestoreDisplay = RestoreDisplayCheck.IsChecked == true;
        return true;
    }

    private void DetectCpuInfo()
    {
        string cpuName = "Unbekannte CPU";
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0");
            cpuName = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? cpuName;
        }
        catch { }
        CpuDetectedText.Text = $"{cpuName} · {Environment.ProcessorCount} logische Prozessoren";
    }

    private void DetectCpuButton_Click(object sender, RoutedEventArgs e)
    {
        DetectCpuInfo();
        string cpuName = "Unbekannte CPU";
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0");
            cpuName = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? cpuName;
        }
        catch { }
        CpuProfileBox.Text = cpuName;
        int logical = Environment.ProcessorCount;

        // CPU 0 und CPU 1 bewusst aus der CS2-Affinity ausschließen.
        // Bei 32 oder mehr logischen Prozessoren bleibt die bestehende 32-Bit-Maskenbegrenzung bestehen.
        int allLogicalMask = logical >= 31 ? int.MaxValue : (1 << logical) - 1;
        int mask = allLogicalMask & ~0b11;

        if (mask <= 0)
        {
            MessageBox.Show("Es müssen mindestens drei logische Prozessoren verfügbar sein, damit CPU 0 und CPU 1 ausgeschlossen werden können.",
                "CPU-Erkennung", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AffinityMaskBox.Text = mask.ToString(CultureInfo.InvariantCulture);
        AddLog($"CPU erkannt: {cpuName}; {logical} logische Prozessoren. CPU 0 und CPU 1 werden ausgeschlossen. " +
               $"CS2-Affinity: {mask} (0x{mask:X}) – {CountBits((ulong)mask)} logische Prozessoren aktiv.");
    }

    private void LoadRyzenProfileButton_Click(object sender, RoutedEventArgs e)
    {
        CpuProfileBox.Text = "Ryzen 7 7800X3D";
        AffinityMaskBox.Text = "65532";
        PriorityCombo.SelectedIndex = 0;
        SetAffinityCheck.IsChecked = true;
        SetPriorityCheck.IsChecked = true;
        AddLog("7800X3D-Profil geladen. Prüfe die logische CPU-Topologie vor dem Einsatz.");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadSettingsFromUi()) return;
        try { _settings.Save(); AddLog("Einstellungen gespeichert."); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Speicherfehler", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ------------------------------------------------------------------ Start-Ablauf

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!ReadSettingsFromUi()) return;
        try { _settings.Save(); }
        catch (Exception ex) { AddLog("Einstellungen konnten nicht gespeichert werden: " + ex.Message); }

        // Alle Rückfragen vor der ersten Systemänderung klären.
        ConnectTarget? connectTarget = null;
        string connectText = ConnectBox.Text.Trim();
        if (connectText.Length > 0)
        {
            if (!Cs2ManagerService.TryParseConnect(connectText, out connectTarget, out string connectError))
            {
                MessageBox.Show(connectError, "Connect-Befehl", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        bool switchDisplay = false;
        if (_settings.AskForSingleDisplay && Forms.Screen.AllScreens.Length > 1)
        {
            switchDisplay = MessageBox.Show(
                "Soll nur der Hauptbildschirm aktiviert werden?\nDer ursprüngliche Desktop wird nach CS2 wieder erweitert.",
                "Display-Konfiguration", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        _busy = true;
        StartButton.IsEnabled = false;
        TopStatus.Text = "WIRD GESTARTET";
        _state = new RunState();
        _runCts = new CancellationTokenSource();
        CancellationToken token = _runCts.Token;
        bool useBuiltinCleaner = false;
        CancellationTokenSource? connectCts = null;

        try
        {
            AddLog("Vorbereitung gestartet.");

            if (switchDisplay)
            {
                try
                {
                    _service.SwitchToInternalDisplay();
                    _state.DisplayWasChanged = true;
                    _state.Persist();
                }
                catch (Exception ex) { AddLog("DisplaySwitch-Fehler: " + ex.Message); }
                await Task.Delay(1500, token);
            }
            token.ThrowIfCancellationRequested();

            // Die kurzen Vorbereitungsschritte laufen ohne Abbruch durch, damit der gespeicherte Zustand stimmt.
            if (_settings.StopOneDrive)
            {
                StoppedProgram? oneDrive = await _service.StopOneDriveAsync(CancellationToken.None);
                if (oneDrive != null)
                {
                    _state.StoppedPrograms.Add(oneDrive);
                    _state.Persist();
                }
            }

            if (_settings.StopExtraProcesses && _settings.ExtraProcesses.Length > 0)
            {
                await _service.StopProgramsAsync(Cs2ManagerService.ParseProcessList(_settings.ExtraProcesses),
                    program => { _state.StoppedPrograms.Add(program); _state.Persist(); },
                    CancellationToken.None);
            }

            if (_settings.StopWindowsSearch)
            {
                bool stopped = await Task.Run(() => _service.StopWindowsSearchIfRunning());
                if (stopped)
                {
                    _state.WindowsSearchWasStopped = true;
                    _state.Persist();
                }
            }

            useBuiltinCleaner = PrepareCleaner();
            token.ThrowIfCancellationRequested();

            string tcpTargetLevel = _settings.TcpAutoTuningLevel;
            if (_settings.EnableTcpAutoTuning && tcpTargetLevel != "unchanged")
            {
                string? previousLevel = TcpAutoTuning.ReadCurrentLevel(out string tcpReadOutput);
                if (previousLevel == null)
                    AddLog("TCP Auto-Tuning: aktueller Windows-Level konnte nicht sicher erkannt werden: " + tcpReadOutput);
                else if (!string.Equals(previousLevel, tcpTargetLevel, StringComparison.OrdinalIgnoreCase))
                {
                    // Vor Änderung sichern, damit auch bei Abbruch/Fehler der ursprüngliche Wert zurückgesetzt werden kann.
                    _state.OriginalTcpAutoTuning = previousLevel;
                    _state.Persist();
                    if (TcpAutoTuning.SetLevel(tcpTargetLevel, out string tcpSetOutput))
                        AddLog($"TCP Auto-Tuning während CS2 auf '{tcpTargetLevel}' gesetzt (vorher: {previousLevel}).");
                    else
                    {
                        _state.OriginalTcpAutoTuning = null;
                        _state.Persist();
                        AddLog("TCP Auto-Tuning konnte nicht geändert werden: " + tcpSetOutput);
                    }
                }
                else AddLog($"TCP Auto-Tuning steht bereits auf '{tcpTargetLevel}' – keine Änderung nötig.");
            }
            else AddLog("TCP Auto-Tuning bleibt unverändert.");

            // CS2 immer ohne Connect-Startparameter starten; Verbindung ausschließlich verzögert per Steam-Link.
            bool cs2AlreadyRunning = _service.IsProcessRunning("cs2");
            _service.LaunchCs2(null);
            TopStatus.Text = "WARTE AUF CS2";
            GameStatus.Text = "WIRD GESUCHT";
            _cs2 = await _service.WaitForCs2Async(_settings.StartTimeoutSeconds, token);
            if (_cs2 == null)
            {
                AddLog($"CS2 wurde innerhalb von {_settings.StartTimeoutSeconds} Sekunden nicht erkannt.");
                return;
            }

            PidText.Text = $"PID: {_cs2.Id}";
            GameStatus.Text = "WIRD OPTIMIERT";
            if (_settings.EnableTimerResolution)
            {
                if (TimerResolutionManager.TryEnable(_settings.TimerResolution100Ns, out int actual, out string timerMessage))
                    AddLog($"Windows Timer-Auflösung angefordert: {_settings.TimerResolution100Ns / 10000.0:0.0} ms; tatsächlich: {actual / 10000.0:0.####} ms. {timerMessage}");
                else
                    AddLog("Timer-Auflösung konnte nicht gesetzt werden: " + timerMessage);
            }
            if (useBuiltinCleaner) StartBuiltinCleaner();
            MinimizeAfterGameStart();

            if (connectTarget != null)
            {
                int delaySeconds = cs2AlreadyRunning ? 0 : _settings.ConnectDelaySeconds;
                connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                _ = SendConnectLaterAsync(connectTarget, delaySeconds, connectCts.Token);
            }

            await _service.ConfigureWhenLoadedAsync(_cs2, _settings.AffinityMask, _settings.Priority,
                _settings.SetAffinity, _settings.SetPriority, _settings.LoadWaitSeconds,
                _settings.SecondPassDelaySeconds, null, token);
        }
        catch (OperationCanceledException) { AddLog("Vorgang abgebrochen."); }
        catch (Exception ex) { AddLog("Fehler: " + ex.Message); }
        finally
        {
            connectCts?.Cancel();
            connectCts?.Dispose();
            TopStatus.Text = "AUFRÄUMEN";
            try { await CleanupAsync(_state); }
            catch (Exception ex) { AddLog("Cleanup-Fehler: " + ex.Message); }
            _state = new RunState();

            _cs2?.Dispose();
            _cs2 = null;
            _runCts?.Dispose();
            _runCts = null;
            _busy = false;
            StartButton.IsEnabled = true;
            TopStatus.Text = "BEREIT";
            GameStatus.Text = "NICHT GESTARTET";
            PidText.Text = "PID: —";

            if (_closeRequested) Close();
        }
    }

    // Sendet den Connect per Steam-Link. Nie nach CS2-Ende: ein steam://connect würde CS2 sonst neu starten.
    private async Task SendConnectLaterAsync(ConnectTarget target, int delaySeconds, CancellationToken token)
    {
        try
        {
            if (delaySeconds > 0)
            {
                AddLog($"Connect zu {target.Address} wird in {delaySeconds} s per Steam-Startlink gesendet.");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
            }
            if (!_service.IsProcessRunning("cs2"))
            {
                AddLog("Connect abgebrochen: CS2 läuft nicht mehr.");
                return;
            }
            _service.OpenSteamUri(target.ToRunUri());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddLog("Connect fehlgeschlagen: " + ex.Message); }
    }

    // ------------------------------------------------------------------ Standby-Cleaner

    /// <summary>Aktiviert bei Bedarf ausschließlich den integrierten Standby-Cleaner.</summary>
    private bool PrepareCleaner()
    {
        return _settings.CleanerMode != "off";
    }

    private void StartBuiltinCleaner()
    {
        _cleanerCts = new CancellationTokenSource();
        _cleanerTask = _cleaner.RunAsync(_settings.CleanerStandbyMb, _settings.CleanerFreeMb,
            _settings.CleanerIntervalSeconds, _cleanerCts.Token);
    }

    private async Task StopBuiltinCleanerAsync()
    {
        if (_cleanerCts == null) return;
        _cleanerCts.Cancel();
        try { if (_cleanerTask != null) await _cleanerTask; } catch { }
        _cleanerCts.Dispose();
        _cleanerCts = null;
        _cleanerTask = null;
    }

    private void PurgeNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!StandbyListCleaner.TryEnablePrivilege(out string privilegeError))
        {
            AddLog("Standby-Liste: " + privilegeError);
            return;
        }
        if (!StandbyListCleaner.TryReadMemoryLists(out long freeBefore, out long standbyBefore))
        {
            AddLog("Standby-Liste: Speicherlisten konnten nicht gelesen werden.");
            return;
        }
        if (!StandbyListCleaner.TryPurge(out string purgeError))
        {
            AddLog("Standby-Liste konnte nicht geleert werden: " + purgeError);
            return;
        }
        StandbyListCleaner.TryReadMemoryLists(out long freeAfter, out long standbyAfter);
        AddLog($"Standby-Liste geleert: {standbyBefore} MB → {standbyAfter} MB (frei: {freeBefore} MB → {freeAfter} MB).");
    }

    // ------------------------------------------------------------------ Cleanup und Wiederherstellung

    private async Task CleanupAsync(RunState state)
    {
        AddLog("Cleanup gestartet.");
        if (TimerResolutionManager.IsEnabled)
        {
            TimerResolutionManager.Disable(out string timerMessage);
            AddLog("Windows Timer-Auflösung freigegeben. " + timerMessage);
        }
        await StopBuiltinCleanerAsync();

        if (!string.IsNullOrWhiteSpace(state.OriginalTcpAutoTuning))
        {
            if (TcpAutoTuning.SetLevel(state.OriginalTcpAutoTuning, out string tcpRestoreOutput))
                AddLog($"TCP Auto-Tuning auf ursprünglichen Wert '{state.OriginalTcpAutoTuning}' zurückgesetzt.");
            else
                AddLog("TCP Auto-Tuning konnte nicht wiederhergestellt werden: " + tcpRestoreOutput);
            state.OriginalTcpAutoTuning = null;
            state.Persist();
        }

        if (state.DisplayWasChanged && _settings.RestoreDisplay)
        {
            try { _service.RestoreExtendedDisplay(); }
            catch (Exception ex) { AddLog("Display-Wiederherstellung fehlgeschlagen: " + ex.Message); }
        }

        if (state.StoppedPrograms.Count > 0) await Task.Delay(1200);
        foreach (StoppedProgram program in state.StoppedPrograms)
        {
            bool restart = program.Name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
                ? _settings.RestartOneDrive
                : _settings.RestartExtraProcesses;
            if (!restart)
            {
                AddLog($"{program.Name} bleibt beendet (Neustart deaktiviert).");
                continue;
            }
            if (string.IsNullOrWhiteSpace(program.FilePath))
            {
                AddLog($"{program.Name}: Pfad unbekannt – Neustart nicht möglich.");
                continue;
            }

            try
            {
                if (_service.IsProcessRunning(program.Name))
                    AddLog($"{program.Name} läuft bereits wieder.");
                else
                    _service.StartAsUser(program.FilePath, program.Arguments);
            }
            catch (Exception ex) { AddLog($"{program.Name}: Neustart fehlgeschlagen: {ex.Message}"); }
        }

        if (state.WindowsSearchWasStopped)
        {
            try { await Task.Run(() => _service.RestoreWindowsSearch()); }
            catch (Exception ex) { AddLog("Windows-Search-Wiederherstellung fehlgeschlagen: " + ex.Message); }
        }

        RunState.Delete();
        AddLog("Cleanup abgeschlossen.");
    }

    // Stellt Änderungen eines früheren Laufs wieder her, der nicht sauber beendet wurde (Absturz, Task-Manager, Shutdown).
    private async Task RecoverPendingStateAsync()
    {
        RunState? pending = RunState.LoadPending();
        if (pending == null || pending.IsEmpty)
        {
            RunState.Delete();
            return;
        }
        if (_busy) return;

        AddLog("Ein früherer Lauf wurde nicht sauber beendet – stelle die Änderungen wieder her.");
        _busy = true;
        StartButton.IsEnabled = false;
        try { await CleanupAsync(pending); }
        catch (Exception ex) { AddLog("Wiederherstellung fehlgeschlagen: " + ex.Message); }
        finally
        {
            _busy = false;
            StartButton.IsEnabled = true;
            if (_closeRequested) Close();
        }
    }

    // ------------------------------------------------------------------ Monitoring (nur bei sichtbarem Fenster)

    private void MonitoringCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateMonitorTimer();
        if (EnableMonitoringCheck.IsChecked != true) ShowNoGame();
    }

    private void UpdateMonitorTimer()
    {
        bool shouldRun = EnableMonitoringCheck.IsChecked == true && WindowState != WindowState.Minimized;
        if (shouldRun)
        {
            if (!_monitorTimer.IsEnabled)
            {
                _monitorTimer.Start();
                UpdateMetrics();
            }
        }
        else
        {
            _monitorTimer.Stop();
        }
    }

    private void ShowNoGame()
    {
        CpuText.Text = "— %"; CpuBar.Value = 0; RamText.Text = "— GB";
        AffinityValue.Text = "—"; PriorityValue.Text = "—"; CoreValue.Text = "—";
        if (!_busy) { GameStatus.Text = "NICHT GESTARTET"; PidText.Text = "PID: —"; }
    }

    private static bool SafeHasExited(Process process)
    {
        try { process.Refresh(); return process.HasExited; }
        catch { return true; }
    }

    private void DisposeMonitorProcess()
    {
        _monitorProcess?.Dispose();
        _monitorProcess = null;
    }

    private void UpdateMetrics()
    {
        try
        {
            if (_monitorProcess == null || SafeHasExited(_monitorProcess))
            {
                DisposeMonitorProcess();
                Process[] candidates = Process.GetProcessesByName("cs2");
                if (candidates.Length == 0)
                {
                    ShowNoGame();
                    return;
                }

                _monitorProcess = candidates[0];
                for (int i = 1; i < candidates.Length; i++)
                    candidates[i].Dispose();

                _lastCpuTime = _monitorProcess.TotalProcessorTime;
                _lastCpuSample = DateTime.UtcNow;
            }

            Process game = _monitorProcess!;
            PidText.Text = $"PID: {game.Id}";
            if (!_busy) GameStatus.Text = "LÄUFT";
            RamText.Text = $"{game.WorkingSet64 / 1024d / 1024d / 1024d:0.00} GB";

            var now = DateTime.UtcNow;
            var cpuNow = game.TotalProcessorTime;
            double elapsed = (now - _lastCpuSample).TotalSeconds;
            if (elapsed >= 0.5)
            {
                double cpu = (cpuNow - _lastCpuTime).TotalSeconds / elapsed / Environment.ProcessorCount * 100;
                cpu = Math.Clamp(cpu, 0, 100);
                CpuText.Text = $"{cpu:0.0} %";
                CpuBar.Value = cpu;
                _lastCpuTime = cpuNow;
                _lastCpuSample = now;
            }

            try
            {
                long affinity = game.ProcessorAffinity.ToInt64();
                AffinityValue.Text = $"{affinity}  (0x{affinity:X})";
                PriorityValue.Text = game.PriorityClass.ToString();
                CoreValue.Text = $"{CountBits((ulong)affinity)} / {Environment.ProcessorCount}";
            }
            catch
            {
                AffinityValue.Text = "Zugriff verweigert";
                PriorityValue.Text = "Zugriff verweigert";
                CoreValue.Text = "—";
            }
        }
        catch { }
    }

    private static int CountBits(ulong value)
    {
        int count = 0;
        while (value != 0) { count += (int)(value & 1); value >>= 1; }
        return count;
    }

    // ------------------------------------------------------------------ Log, Schließen

    private void AddLog(string message)
    {
        LogBox.AppendText(message + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Während ein Lauf aktiv ist, wird erst aufgeräumt (OneDrive, Windows Search, Display ...) und dann geschlossen.
        if (_busy)
        {
            if (!_closeRequested)
            {
                bool gameRunning = _cs2 != null && !SafeHasExited(_cs2);
                if (gameRunning)
                {
                    MessageBoxResult answer = MessageBox.Show(
                        "CS2 läuft noch. Beim Beenden stellt der Manager alle Änderungen wieder her " +
                        "(OneDrive, Windows Search, Bildschirm …), während das Spiel weiterläuft.\n\nTrotzdem beenden?",
                        "CS2 Manager beenden", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes)
                    {
                        e.Cancel = true;
                        return;
                    }
                }

                _closeRequested = true;
                AddLog("Beenden angefordert – Änderungen werden zuerst wiederhergestellt.");
                TopStatus.Text = "AUFRÄUMEN";
                _runCts?.Cancel();
            }
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _monitorTimer.Stop();
        DisposeMonitorProcess();
        _cleanerCts?.Cancel();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        base.OnClosed(e);
    }
}
