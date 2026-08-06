using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Aqst.OesSpectrometer.Models;
using Microsoft.Win32;
using OxyPlot;

namespace OES_Leak_Monitor;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    // The one and only place the app folder name is spelled out — everything else
    // takes the resolved paths from this instance.
    private readonly OesAppPaths _paths = new("OES_Leak_Monitor");
    private readonly SettingsService _settingsService;
    private readonly SystemLogger _systemLogger;
    private readonly DualIntensityLogger _intensityLogger;
    private readonly LeakMonitorEngine _leakMonitorEngine;
    private readonly RatioCsvLogger _ratioCsvLogger;
    private readonly List<DeviceViewModel> _devices;

    // Test-mode plasma-spectrum playback: when an operator picks a full-spectrum CSV it
    // replaces the device's built-in synthetic frames (a no-op for real hardware or when
    // no file is loaded). Every device frame is routed through this before the consumers.
    private readonly SpectrumSimulationSource _simulation = new();

    // Tracks the OES acquisition state so a Stop→Start transition applies a staged
    // Ratio Setup configuration. Single-device app, so one flag suffices.
    private bool _wasAcquiring;

    // Data-folder housekeeping: compress expired day folders (never delete) and warn about
    // free space. Runs off the UI thread on a slow timer; the results are surfaced in the
    // Configuration tab and in the start-up / shutdown warning.
    private readonly System.Windows.Threading.Dispatcher _dispatcher =
        System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private DataRetentionSettings _retention = new();
    private System.Threading.Timer? _retentionTimer;
    private int _retentionRunning;   // 0/1 guard so a slow pass can't overlap the next tick

    // Data directory the Recordings / Ratio Review tabs last scanned. They resolve it from
    // the logger and scan once, so an Apply that repoints the output folder has to tell them
    // to rescan — otherwise both tabs keep listing the old folder's files until someone
    // presses Refresh.
    private string _lastDataDirectory = "";

    // The logger's armed flag as it stands in settings.json. The live flag can be flipped
    // from the Configuration tab without saving, so the two can disagree — and the
    // difference matters: an unsaved arm silently reverts on the next launch. Kept in sync
    // at load and on every Save; the Monitor strip warns while they differ.
    private bool _persistedLoggerEnabled;

    // Per-device colors and labels. Single OES for leak monitoring. Add tuples here
    // if you grow to multi-device — the rest of the wiring loops over this array.
    // Tag "OES1" is kept (rather than just "OES") so the recordings infrastructure
    // (RecordingGroup pairing, filename scan) keeps working without changes.
    private static readonly (string Name, OxyColor Color, string Tag)[] DeviceProfiles =
    {
        ("OES", OxyColors.SteelBlue, "OES1"),
    };

    public MainViewModel()
    {
        _settingsService = new SettingsService(_paths.ConfigDirectory);
        _systemLogger    = new SystemLogger(_paths.LogDirectory);

        var deviceTags = DeviceProfiles.Select(p => p.Tag).ToArray();
        _intensityLogger = new DualIntensityLogger(deviceTags, _paths.DataDirectory);

        Logger      = new LoggerViewModel(_intensityLogger, _paths.DataDirectory);
        LogViewer   = new LogViewerViewModel(_systemLogger);

        // Hydrate the logger from settings.json BEFORE building the review tabs. Both of
        // them read the logger in their constructors — Recordings/RatioReview scan
        // EffectiveBaseDirectory once, and Recordings also snapshots the trigger wavelength
        // for its plot title/series. Built against an empty LoggerViewModel they would take
        // the factory defaults: the AppData fallback folder instead of the configured data
        // directory (so both tabs listed "0 files" until the user pressed Refresh) and
        // 337 nm instead of the configured trigger wavelength.
        var settings = _settingsService.Load();
        Logger.LoadFrom(settings.Logger);
        _retention = settings.DataRetention ?? new DataRetentionSettings();
        _persistedLoggerEnabled = settings.Logger.Enabled;
        // The Monitor tab mirrors the logger's armed flag / state / open file read-only, so
        // an Operator — who cannot open the Engineer-gated Configuration tab where the
        // LoggerPanel lives — can still see whether anything is being recorded.
        Logger.PropertyChanged += OnLoggerPropertyChanged;
        _lastDataDirectory = LoggerSettings.ResolveBaseDirectory(
            settings.Logger.BaseDirectory, _paths.DataDirectory);

        Recordings  = new RecordingsViewModel(Logger, _intensityLogger, _paths.DataDirectory);
        RatioReview = new RatioReviewViewModel(Logger, _intensityLogger, _paths.DataDirectory);

        _devices = new List<DeviceViewModel>(DeviceProfiles.Length);
        for (int i = 0; i < DeviceProfiles.Length; i++)
        {
            var (name, color, _) = DeviceProfiles[i];
            var vm = new DeviceViewModel(name, color, _systemLogger);
            // SpectrumAvailable is wired below, once all consumers (logger, leak engine,
            // Monitor-tab trend) exist, so a single handler can fan one effective frame out.
            vm.PropertyChanged += OnDevicePropertyChanged;
            _devices.Add(vm);
        }
        Devices = _devices.AsReadOnly();

        // Hook the intensity logger lifecycle into the system log so file open / close
        // and state machine transitions land in the audit CSV.
        _intensityLogger.StateChanged   += OnIntensityStateChanged;
        _intensityLogger.ErrorOccurred  += OnIntensityError;
        _intensityLogger.FilesChanged   += OnIntensityFilesChanged;
        _intensityLogger.FileRolled     += OnIntensityFileRolled;

        // Devices exist now, so the persisted per-device parameters can be applied. (The
        // settings themselves were loaded above, before the review tabs were built.)
        ApplySettingsToDevices(settings);

        // Restore the last-used Test-mode simulation file (if it still exists on disk).
        // A missing/unparseable file silently falls back to the synthetic generator.
        if (!string.IsNullOrWhiteSpace(settings.SimulationCsvPath) && File.Exists(settings.SimulationCsvPath))
        {
            try { _simulation.Load(settings.SimulationCsvPath); }
            catch (Exception ex) { _systemLogger.LogError("SimulationFile_Load_Failed", ex, settings.SimulationCsvPath); }
        }

        // Monitor tab: a live intensity time-trend at the "selected" wavelength — i.e. the
        // trigger (threshold) wavelength configured in the LoggerPanel — plus the first few
        // monitored wavelengths logged into the intensity CSV. The chart follows that config:
        // seeded here and re-pointed by ApplyAll whenever it is applied.
        var loggerSettings = Logger.ToSettings();
        WavelengthTrend = new WavelengthTrendViewModel(
            loggerSettings.TriggerWavelength, loggerSettings.SaveStartThresholdIntensity,
            loggerSettings.MonitoredWavelengths?.Select(w => (double)w));

        // Actinometry leak monitor: build from persisted config, feed it the same
        // spectrum stream the intensity logger sees, and bridge its lifecycle into
        // the system log. Golden Run captures are persisted as they happen.
        _leakMonitorEngine = new LeakMonitorEngine(settings.LeakMonitor, _systemLogger);
        LeakMonitor = new LeakMonitorViewModel(_leakMonitorEngine, _systemLogger);

        // Single fan-out: each device frame is mapped through the Test-mode simulation
        // (a no-op unless a CSV is loaded and the frame is synthetic) and then handed to
        // the intensity logger, the leak engine, and the Monitor-tab trend — so all three
        // always see the same effective spectrum.
        for (int i = 0; i < _devices.Count; i++)
        {
            int slot = i;
            _devices[i].SpectrumAvailable += (_, sample) => OnDeviceSpectrum(slot, sample);
        }

        _leakMonitorEngine.AlarmStateChanged += OnLeakAlarmStateChanged;
        _leakMonitorEngine.GoldenRunCaptured += OnGoldenRunCaptured;
        _leakMonitorEngine.ConfigurationChanged += OnLeakConfigChanged;

        // Ratio Setup tab: a staged editor for the species-ratio configuration. Saving
        // persists it to settings.json; it is applied to the engine when acquisition
        // (re)starts — see OnDevicePropertyChanged.
        RatioSetup = new RatioSetupViewModel(_leakMonitorEngine,
            () => PersistLeakMonitorSettings("RatioSetupSaved"), _systemLogger);

        // Wavelength Calibration tab: a staged editor for the catalog-level wavelength-drift
        // correction overlay. Like a Ratio Setup edit it is persisted immediately but only
        // applied to the engine when acquisition (re)starts (via ReloadRatios).
        WavelengthCorrection = new WavelengthCorrectionViewModel(_leakMonitorEngine,
            () => PersistLeakMonitorSettings("WavelengthCorrectionsSaved"), _systemLogger);
        // The Ratio Setup line pickers annotate each line with its offset, so refresh them
        // when the overlay changes — without reloading rows, which would drop unsaved edits.
        WavelengthCorrection.CorrectionsSaved += (_, _) => RatioSetup.RefreshCorrections();

        // Leak Calibration tab: a guided wizard that captures "ratio rise ↔ known leak rate"
        // points and fits a per-ratio sensitivity, persisted to settings.json (Engineer+).
        LeakCalibration = new LeakCalibrationViewModel(_leakMonitorEngine,
            () => PersistLeakMonitorSettings("LeakCalibrationSaved"), _systemLogger);

        // Ratio-trend CSV: its own recorder, running for as long as the OES acquires —
        // deliberately NOT tied to the threshold logger's save sessions, so disarming the
        // raw-spectrum recorder (or running below its trigger threshold) no longer throws
        // away the leak history. It only shares the output folder and file prefix.
        _ratioCsvLogger = new RatioCsvLogger(_leakMonitorEngine, _paths.DataDirectory, _systemLogger);
        _ratioCsvLogger.Configure(loggerSettings);

        _systemLogger.LogSystemEvent(LogSeverity.Information, "SettingsLoaded",
            "Loaded settings from disk",
            related: $"Path={_settingsService.ConfigFilePath}",
            value: $"Users={settings.AccessControl.Users.Count}");

        // Access control: persist user-list edits without disturbing any unsaved
        // Device/Logger edits in the Configuration tab — reload the on-disk settings
        // and swap in only the new AccessControlConfig.
        AccessControl = new AccessControlService(settings.AccessControl, cfg =>
        {
            var onDisk = _settingsService.Load();
            onDisk.AccessControl = cfg;
            _settingsService.Save(onDisk);
        }, _systemLogger);
        AccessControl.RoleChanged += OnRoleChanged;

        // Per-device Connect / Disconnect / Start / Stop are provided by DevicePanel
        // (header buttons bound to DeviceViewModel.ConnectCommand etc.); the LoggerPanel
        // exposes Start Save / Stop Save for the intensity logger. There is no longer a
        // toolbar-level "Both" command — single OES means the per-device buttons suffice.
        ApplyAllCommand        = new RelayCommand(ApplyAll,        () => IsEngineerOrHigher);
        SaveAllCommand         = new RelayCommand(SaveSettings,    () => IsEngineerOrHigher);
        LoadDefaultsAllCommand = new RelayCommand(LoadDefaultsAll, () => IsEngineerOrHigher);
        ResetExperimentCommand = new RelayCommand(ResetExperiment, () => IsOperatorOrHigher);
        ArchiveNowCommand      = new RelayCommand(() => StartRetentionPass(manual: true),
                                                  () => IsEngineerOrHigher && _retentionRunning == 0);
        ChooseSimulationFileCommand = new RelayCommand(ChooseSimulationFile, () => IsEngineerOrHigher);
        ClearSimulationFileCommand  = new RelayCommand(ClearSimulationFile,
            () => IsEngineerOrHigher && _simulation.IsLoaded);

        // Initial role is Guest → propagate the action gate so the per-device buttons
        // start out disabled until the user signs in.
        foreach (var d in _devices) d.ActionsAllowed = IsOperatorOrHigher;
        LeakMonitor.SetRole(IsOperatorOrHigher, IsEngineerOrHigher);
        RatioSetup.SetRole(IsEngineerOrHigher);
        WavelengthCorrection.SetRole(IsEngineerOrHigher);
        LeakCalibration.SetRole(IsEngineerOrHigher);

        UpdateRecorderStatus();

        // First housekeeping pass shortly after start-up (off the UI thread), then every
        // six hours. The delay keeps the window responsive while it is still opening.
        _retentionTimer = new System.Threading.Timer(
            _ => StartRetentionPass(manual: false), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));
    }

    /// <summary>
    /// Reacts to OES acquisition start/stop transitions:
    /// <list type="bullet">
    /// <item>On Stop→Start, the leak engine rebuilds its ratio set from the saved
    /// settings, so a staged Ratio Setup edit takes effect (editing mid-run never
    /// disturbs a live evaluation — Stop then Start to apply).</item>
    /// <item>On Start→Stop, the intensity logger's save session is force-closed via
    /// <see cref="DualIntensityLogger.Stop"/>. The threshold state machine is purely
    /// sample-driven and would otherwise stay parked in <c>Saving</c> with its CSV
    /// open while acquisition is stopped — the next Start, with plasma still above
    /// the threshold, would then keep appending to that stale file. Force-closing
    /// resets the machine to <c>Idle</c> (the logger stays armed, <c>Enabled</c>
    /// untouched) so the next Start opens a fresh Intensity CSV once the threshold
    /// is re-crossed; the Ratio CSV follows via the logger's <c>FilesChanged</c>
    /// event.</item>
    /// </list>
    /// </summary>
    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DeviceViewModel.IsAcquiring)) return;
        if (sender is not DeviceViewModel d) return;

        bool now = d.IsAcquiring;
        if (now && !_wasAcquiring)
        {
            _leakMonitorEngine.ReloadRatios();
            _systemLogger.LogSystemEvent(LogSeverity.Information, "LeakMonitorRatiosApplied",
                "Species-ratio configuration (re)applied on OES acquisition start");
        }
        else if (!now && _wasAcquiring)
        {
            // Close both recorders' sessions so a Stop genuinely ends the recording — the
            // next Start gets new files. They are closed independently now: the Intensity
            // CSV is threshold-driven, the Ratio CSV runs for the whole acquisition.
            _intensityLogger.Stop();
            _ratioCsvLogger.Stop();
            _systemLogger.LogSystemEvent(LogSeverity.Information, "IntensityLoggerSessionEnded",
                "Intensity/Ratio save session closed because OES acquisition stopped");
        }
        _wasAcquiring = now;
    }

    /// <summary>
    /// The single fan-out for every device spectrum frame. Maps the raw frame through the
    /// Test-mode simulation (returns it unchanged unless a playback CSV is loaded and the
    /// frame is synthetic), then forwards the effective frame to the intensity logger, the
    /// leak engine, and the Monitor-tab trend. Runs on the device's acquisition thread.
    /// </summary>
    private void OnDeviceSpectrum(int slot, SpectrumSample raw)
    {
        var sample = _simulation.Map(raw);
        _intensityLogger.ProcessSample(slot, sample);
        _leakMonitorEngine.ProcessSample(sample);
        WavelengthTrend.OnSpectrum(sample);
    }

    public AccessControlService AccessControl { get; }

    public bool IsOperatorOrHigher => AccessControl.CurrentRole >= UserRole.Operator;
    public bool IsEngineerOrHigher => AccessControl.CurrentRole >= UserRole.Engineer;
    public bool IsAdmin            => AccessControl.CurrentRole == UserRole.Admin;

    /// <summary>Display string for the toolbar user badge — bare "Guest" when nobody is signed in.</summary>
    public string CurrentUserText =>
        AccessControl.CurrentUsername is null
            ? AccessControl.CurrentRole.ToString()
            : $"{AccessControl.CurrentUsername} ({AccessControl.CurrentRole})";

    private void OnRoleChanged(object? sender, UserRole _)
    {
        OnPropertyChanged(nameof(CurrentUserText));
        OnPropertyChanged(nameof(IsOperatorOrHigher));
        OnPropertyChanged(nameof(IsEngineerOrHigher));
        OnPropertyChanged(nameof(IsAdmin));

        foreach (var d in _devices) d.ActionsAllowed = IsOperatorOrHigher;
        LeakMonitor.SetRole(IsOperatorOrHigher, IsEngineerOrHigher);
        RatioSetup.SetRole(IsEngineerOrHigher);
        WavelengthCorrection.SetRole(IsEngineerOrHigher);
        LeakCalibration.SetRole(IsEngineerOrHigher);

        RaiseCanExec();
    }

    private void ApplyAll()
    {
        // Each underlying command self-gates (e.g. device Apply needs IsConnected).
        // Skip rather than block — user can apply just the logger config without devices connected.
        foreach (var d in _devices)
            if (d.ApplyParamsCommand.CanExecute(null)) d.ApplyParamsCommand.Execute(null);
        Logger.ApplyCommand.Execute(null);
        // Re-point the Monitor-tab trend at the (possibly edited) trigger + monitored wavelengths.
        var ls = Logger.ToSettings();
        // The ratio CSV shares the intensity logger's folder and prefix — an edited path
        // takes effect on its next session, not mid-file.
        _ratioCsvLogger.Configure(ls);
        // Repointed output folder → the review tabs are now listing the wrong tree. Rescan
        // only on an actual change; a scan walks every day folder under the base directory.
        var dataDir = LoggerSettings.ResolveBaseDirectory(ls.BaseDirectory, _paths.DataDirectory);
        if (!string.Equals(dataDir, _lastDataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _lastDataDirectory = dataDir;
            Recordings.Refresh();
            RatioReview.Refresh();
            _systemLogger.LogSystemEvent(LogSeverity.Information, "DataDirectoryChanged",
                "Logger output folder changed — Recordings / Ratio Review rescanned",
                value: dataDir);
        }
        WavelengthTrend.Configure(ls.TriggerWavelength, ls.SaveStartThresholdIntensity,
            ls.MonitoredWavelengths?.Select(w => (double)w));
        StatusMessage = "Apply: parameters pushed to connected devices and logger.";
        _systemLogger.LogSystemEvent(LogSeverity.Information, "ApplyAll",
            "User pushed configuration to devices and logger",
            related: $"User={AccessControl.CurrentUsername ?? "(guest)"}");
    }

    /// <summary>
    /// Monitor-tab "Reset Run": starts a fresh experiment after a parameter change without
    /// stopping acquisition or touching any configuration. It (1) force-closes the current
    /// Intensity save session — the next above-threshold frame opens a new Intensity CSV, and
    /// the Ratio CSV follows in lockstep; (2) clears the Monitor-tab Intensity trend so its
    /// time axis restarts; (3) clears the Leak Monitor % -of-baseline trend and resets each
    /// ratio's live smoothing so pre-change frames don't bleed into the new run. Golden Run
    /// baselines, calibration, ratio configuration, and latched alarms are kept.
    /// </summary>
    private void ResetExperiment()
    {
        // (1) Roll both recorders to fresh files. The threshold logger stays armed — Stop()
        // force-closes it to Idle so the next threshold cross opens a new Intensity CSV;
        // the Ratio CSV, which is not threshold-driven, reopens on the very next frame.
        _intensityLogger.Stop();
        _ratioCsvLogger.Stop();

        // (2) Restart the live Monitor-tab intensity trend (new start time).
        WavelengthTrend.Reset();

        // (3) Restart the Leak Monitor trend + per-ratio smoothing; keep latched alarms so a
        // real, already-confirmed leak isn't silently cleared (operator must Acknowledge).
        LeakMonitor.ResetTrend();
        _leakMonitorEngine.ResetRuntimeState(clearAlarms: false);

        StatusMessage = "Reset: a new log file opens on the next above-threshold frame; trends cleared.";
        _systemLogger.LogSystemEvent(LogSeverity.Information, "ExperimentReset",
            "Operator reset the run — new Intensity/Ratio CSV on next threshold cross; " +
            "Monitor trends and per-ratio smoothing cleared (baselines/calibration/alarms kept)",
            related: $"User={AccessControl.CurrentUsername ?? "(guest)"}");
    }

    private void LoadDefaultsAll()
    {
        foreach (var d in _devices) d.LoadDefaultsCommand.Execute(null);
        Logger.LoadDefaults();
        StatusMessage = "Defaults loaded — click Apply to push to devices and logger.";
        _systemLogger.LogSystemEvent(LogSeverity.Information, "LoadDefaultsAll",
            "Reset to factory defaults (not yet applied/persisted)",
            related: $"User={AccessControl.CurrentUsername ?? "(guest)"}");
    }

    /// <summary>
    /// Lets the operator pick a full-spectrum CSV (same format the intensity logger writes)
    /// to play back as the spectrum stream while in Test Mode. The chosen path is loaded
    /// immediately and persisted so it is reused on the next launch. A parse failure leaves
    /// the previous source untouched. Real-hardware frames ignore the simulation entirely.
    /// </summary>
    private void ChooseSimulationFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a full-spectrum CSV to play back in Test Mode",
            Filter = "Spectrum CSV (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (Directory.Exists(_paths.DataDirectory)) dlg.InitialDirectory = _paths.DataDirectory;
        if (dlg.ShowDialog() != true) return;

        try
        {
            _simulation.Load(dlg.FileName);
            PersistSimulationPath();
            OnPropertyChanged(nameof(SimulationFileText));
            ClearSimulationFileCommand.RaiseCanExecuteChanged();
            StatusMessage = $"Test-mode simulation loaded: {Path.GetFileName(dlg.FileName)} " +
                            $"({_simulation.FrameCount} frames, loops).";
            _systemLogger.LogSystemEvent(LogSeverity.Information, "SimulationFileSelected",
                "Test-mode plasma-spectrum playback file selected",
                related: $"User={AccessControl.CurrentUsername ?? "(guest)"}",
                value: $"Path={dlg.FileName},Frames={_simulation.FrameCount}");
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not load simulation file: " + ex.Message;
            _systemLogger.LogError("SimulationFile_Load_Failed", ex, dlg.FileName);
        }
    }

    /// <summary>Drops the loaded Test-mode simulation file (reverting to the built-in
    /// synthetic generator) and persists the cleared selection.</summary>
    private void ClearSimulationFile()
    {
        _simulation.Clear();
        PersistSimulationPath();
        OnPropertyChanged(nameof(SimulationFileText));
        ClearSimulationFileCommand.RaiseCanExecuteChanged();
        StatusMessage = "Test-mode simulation cleared — using built-in synthetic spectra.";
        _systemLogger.LogSystemEvent(LogSeverity.Information, "SimulationFileCleared",
            "Test-mode plasma-spectrum playback file cleared",
            related: $"User={AccessControl.CurrentUsername ?? "(guest)"}");
    }

    /// <summary>
    /// Persists the simulation-file path immediately — re-reads on-disk settings and swaps
    /// in only that field, so an unsaved Configuration-tab edit is not clobbered (mirrors
    /// how AccessControl and leak-monitor edits are persisted).
    /// </summary>
    private void PersistSimulationPath()
    {
        try
        {
            var onDisk = _settingsService.Load();
            onDisk.SimulationCsvPath = _simulation.FilePath;
            _settingsService.Save(onDisk);
        }
        catch (Exception ex)
        {
            _systemLogger.LogError("SimulationPath_Persist_Failed", ex, _simulation.FilePath ?? "(none)");
        }
    }

    /// <summary>
    /// Persist all devices' parameters and the shared logger settings as one JSON
    /// file. Backs the unified Save button at the bottom of the Configuration tab.
    /// </summary>
    private void SaveSettings()
    {
        var settings = new AppSettings
        {
            Devices       = _devices.Select(d => d.ToSettings()).ToList(),
            Logger        = Logger.ToSettings(),
            LeakMonitor   = _leakMonitorEngine.Settings, // includes captured Golden Runs
            AccessControl = AccessControl.SnapshotConfig(), // preserve user list across saves
            DataRetention = _retention,
            SimulationCsvPath = _simulation.FilePath, // keep the Test-mode playback selection
        };
        _settingsService.Save(settings);
        // The armed flag now matches what is on disk, so the Monitor strip's
        // "not saved" warning clears.
        _persistedLoggerEnabled = settings.Logger.Enabled;
        UpdateRecorderStatus();
        StatusMessage = "Settings saved to " + _settingsService.ConfigFilePath;
        _systemLogger.LogSystemEvent(LogSeverity.Information, "SettingsSaved",
            "Settings written to disk",
            related: $"User={AccessControl.CurrentUsername ?? "(guest)"}",
            value: $"Path={_settingsService.ConfigFilePath}");
    }

    private void ApplySettingsToDevices(AppSettings settings)
    {
        for (int i = 0; i < _devices.Count; i++)
        {
            if (i < settings.Devices.Count)
                _devices[i].ApplySettings(settings.Devices[i]);
        }
    }

    public IReadOnlyList<DeviceViewModel> Devices { get; }
    public LoggerViewModel      Logger      { get; }
    public LogViewerViewModel   LogViewer   { get; }
    public RecordingsViewModel  Recordings  { get; }
    public RatioReviewViewModel RatioReview { get; }
    public LeakMonitorViewModel LeakMonitor { get; }
    public RatioSetupViewModel  RatioSetup  { get; }
    public WavelengthCorrectionViewModel WavelengthCorrection { get; }
    public LeakCalibrationViewModel LeakCalibration { get; }
    public WavelengthTrendViewModel WavelengthTrend { get; }

    public RelayCommand ApplyAllCommand { get; }
    public RelayCommand SaveAllCommand { get; }
    public RelayCommand LoadDefaultsAllCommand { get; }
    public RelayCommand ResetExperimentCommand { get; }
    public RelayCommand ChooseSimulationFileCommand { get; }
    public RelayCommand ClearSimulationFileCommand { get; }

    /// <summary>Configuration-tab readout for the Test-mode simulation source: the loaded
    /// CSV's name and frame count, or a note that the built-in synthetic generator is used.</summary>
    public string SimulationFileText =>
        _simulation.IsLoaded
            ? $"{Path.GetFileName(_simulation.FilePath)} · {_simulation.FrameCount} frames (loops)"
            : "(built-in synthetic spectra)";

    private string _statusMessage = "Ready";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    // --- Monitor-tab recorder strip (read-only mirror of the logger) ---
    //
    // The LoggerPanel — with Start Save / Stop Save and every logger setting — lives in the
    // Configuration tab, which is gated to Engineer+. An Operator running the daily check
    // therefore has no way to see whether data is being recorded at all. These four
    // properties project the logger's armed flag, state machine, and open file into a strip
    // on the Monitor tab. Read-only on purpose: seeing the state is an Operator concern,
    // changing it stays an Engineer one.

    private string _recorderStateText = "OFF";
    /// <summary>Short badge text: OFF / ARMED / STARTING / SAVING / STOPPING.</summary>
    public string RecorderStateText { get => _recorderStateText; private set => Set(ref _recorderStateText, value); }

    private Brush _recorderBrush = Brushes.Gray;
    public Brush RecorderBrush { get => _recorderBrush; private set => Set(ref _recorderBrush, value); }

    private string _recorderDetailText = "";
    /// <summary>One sentence saying what the recorder is doing, naming the open file when there is one.</summary>
    public string RecorderDetailText { get => _recorderDetailText; private set => Set(ref _recorderDetailText, value); }

    private string _recorderWarningText = "";
    /// <summary>
    /// Non-empty only while the live armed flag differs from the one in <c>settings.json</c>.
    /// Arming from the Configuration tab without pressing Save reverts on the next launch —
    /// a silent stop-recording that is otherwise invisible until the data is missing.
    /// </summary>
    public string RecorderWarningText { get => _recorderWarningText; private set => Set(ref _recorderWarningText, value); }

    private string _recorderTooltip = "";
    /// <summary>Full path of the open CSV (or the trigger condition when nothing is open).</summary>
    public string RecorderTooltip { get => _recorderTooltip; private set => Set(ref _recorderTooltip, value); }

    private void OnLoggerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LoggerViewModel.Enabled):
            case nameof(LoggerViewModel.StateText):
            case nameof(LoggerViewModel.CurrentFile1):
            case nameof(LoggerViewModel.TriggerWavelength):
            case nameof(LoggerViewModel.SaveStartThresholdIntensity):
                UpdateRecorderStatus();
                break;
        }
    }

    private void UpdateRecorderStatus()
    {
        var file = Logger.CurrentFile1;
        var hasFile = !string.IsNullOrWhiteSpace(file);
        var trigger = $"{Logger.TriggerWavelength:0.#} nm above {Logger.SaveStartThresholdIntensity:0.#}";

        if (!Logger.Enabled)
        {
            RecorderStateText = "OFF";
            RecorderBrush     = Brushes.Firebrick;
            RecorderDetailText = "Spectrum recording disarmed — the Ratio CSV is still being written.";
            RecorderTooltip    = "The threshold logger is disabled, so no Intensity (full-spectrum) " +
                                 "CSV is written. The Ratio CSV is a separate recorder and keeps " +
                                 "running for as long as the OES acquires. An Engineer arms the " +
                                 "spectrum recorder in the Configuration tab (LoggerPanel → Start " +
                                 "Save), then presses Save so it stays armed after a restart.";
        }
        else
        {
            switch (Logger.StateText)
            {
                case nameof(LoggerState.Saving):
                    RecorderStateText = "SAVING";
                    RecorderBrush     = Brushes.ForestGreen;
                    break;
                case nameof(LoggerState.WaitingToStart):
                    RecorderStateText = "STARTING";
                    RecorderBrush     = Brushes.DarkOrange;
                    break;
                case nameof(LoggerState.WaitingToStop):
                    RecorderStateText = "STOPPING";
                    RecorderBrush     = Brushes.DarkOrange;
                    break;
                default:
                    RecorderStateText = "ARMED";
                    RecorderBrush     = Brushes.SteelBlue;
                    break;
            }
            RecorderDetailText = hasFile
                ? $"Writing {Path.GetFileName(file)}."
                : $"Armed — waiting for {trigger}.";
            RecorderTooltip = hasFile
                ? file
                : $"No file is open. One opens once the intensity at {trigger} is sustained " +
                  $"for {Logger.StartConfirmSeconds:0.#} s.";
        }

        RecorderWarningText = Logger.Enabled == _persistedLoggerEnabled
            ? ""
            : Logger.Enabled
                ? "armed but not saved — reverts to OFF on restart"
                : "disarmed but not saved — returns to ARMED on restart";
    }

    // --- data-folder housekeeping ---------------------------------------------------
    //
    // The program never deletes measurement data. An expired day folder is compressed into
    // a sibling DD.zip — the rows stay recoverable with any unzip tool — which is what makes
    // it safe to run unattended on a production machine. See DataRetentionSettings.

    /// <summary>Whether expired day folders are compressed automatically. Engineer-editable.</summary>
    public bool RetentionEnabled
    {
        get => _retention.Enabled;
        set { if (_retention.Enabled != value) { _retention.Enabled = value; OnPropertyChanged(); } }
    }

    /// <summary>Age at which a day folder is compressed. 0 disables the age rule.</summary>
    public int RetentionArchiveAfterDays
    {
        get => _retention.ArchiveAfterDays;
        set { if (_retention.ArchiveAfterDays != value) { _retention.ArchiveAfterDays = value; OnPropertyChanged(); } }
    }

    /// <summary>Tree size above which compression continues into newer folders. 0 disables it.</summary>
    public double RetentionMaxTotalSizeGB
    {
        get => _retention.MaxTotalSizeGB;
        set { if (_retention.MaxTotalSizeGB != value) { _retention.MaxTotalSizeGB = value; OnPropertyChanged(); } }
    }

    private string _dataFolderStatusText = "Data folder: not scanned yet.";
    /// <summary>One-line summary of the data tree, shown in the Configuration tab.</summary>
    public string DataFolderStatusText { get => _dataFolderStatusText; private set => Set(ref _dataFolderStatusText, value); }

    public RelayCommand ArchiveNowCommand { get; }

    /// <summary>
    /// Free-space / size warnings as of the last inspection, empty when nothing is wrong.
    /// <see cref="MainWindow"/> shows these when the app opens and when it closes.
    /// </summary>
    public IReadOnlyList<string> DataFolderWarnings { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Re-inspect the data folder and refresh <see cref="DataFolderWarnings"/>. Cheap
    /// (directory metadata only) and safe to call from the UI thread — used for the
    /// shutdown warning, where starting a compression pass would be the wrong thing to do.
    /// </summary>
    public DataFolderState InspectDataFolder()
    {
        var state = DataRetentionService.Inspect(_lastDataDirectory, _retention);
        DataFolderWarnings = state.Warnings;
        DataFolderStatusText = DescribeDataFolder(state);
        return state;
    }

    private static string DescribeDataFolder(DataFolderState s)
    {
        if (!s.Exists) return $"Data folder: {s.BaseDirectory} (not created yet)";
        var parts = new List<string>
        {
            $"{DataRetentionService.Gb(s.TotalBytes)} in {s.DayFolderCount} day folder(s)",
        };
        if (s.ArchiveCount > 0)
            parts.Add($"{s.ArchiveCount} archived ({DataRetentionService.Gb(s.ArchivedBytes)})");
        if (s.OldestDay is { } oldest) parts.Add($"oldest {oldest:yyyy-MM-dd}");
        if (s.DriveBytes > 0) parts.Add($"drive {s.FreePercent:0.#}% free");
        return "Data folder: " + string.Join(" · ", parts);
    }

    /// <summary>
    /// Kick off one housekeeping pass on a worker thread. Compression is I/O-heavy and can
    /// take minutes on a large folder, so it must never run on the UI or acquisition thread.
    /// Overlapping passes are suppressed rather than queued.
    /// </summary>
    private void StartRetentionPass(bool manual)
    {
        if (System.Threading.Interlocked.Exchange(ref _retentionRunning, 1) == 1)
        {
            if (manual) StatusMessage = "Data folder housekeeping is already running.";
            return;
        }
        _dispatcher.BeginInvoke(() => ArchiveNowCommand.RaiseCanExecuteChanged());

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var result = DataRetentionService.Run(
                    _lastDataDirectory, _retention,
                    isInUse: IsFileInUseByLogger,
                    report: (evt, message, isError) => _systemLogger.LogSystemEvent(
                        isError ? LogSeverity.Error : LogSeverity.Information,
                        evt, message, related: _lastDataDirectory));

                var state = DataRetentionService.Inspect(_lastDataDirectory, _retention);
                _dispatcher.BeginInvoke(() =>
                {
                    DataFolderWarnings = state.Warnings;
                    DataFolderStatusText = DescribeDataFolder(state);
                    if (result.ArchivedFolders > 0)
                        StatusMessage = $"Data housekeeping: compressed {result.ArchivedFolders} day folder(s), " +
                                        $"reclaimed {DataRetentionService.Gb(result.BytesSaved)}.";
                    else if (manual)
                        StatusMessage = "Data housekeeping: nothing was old enough to compress.";
                });

                if (result.ArchivedFolders > 0 || result.SkippedFolders > 0)
                    _systemLogger.LogSystemEvent(LogSeverity.Information, "DataRetentionPass",
                        $"Compressed {result.ArchivedFolders} folder(s), skipped {result.SkippedFolders}, " +
                        $"reclaimed {DataRetentionService.Gb(result.BytesSaved)}",
                        related: _lastDataDirectory,
                        value: $"Total={DataRetentionService.Gb(result.BytesAfter)}");
                if (result.StillOverCap)
                    _systemLogger.LogSystemEvent(LogSeverity.Warning, "DataRetentionOverCap",
                        "Data folder is still above its size limit after compressing everything eligible — " +
                        "archives need to be moved off this machine.",
                        related: _lastDataDirectory);
                foreach (var w in state.Warnings)
                    _systemLogger.LogSystemEvent(
                        state.CriticalFreeSpace ? LogSeverity.Error : LogSeverity.Warning,
                        "DataFolderWarning", w, related: _lastDataDirectory);
            }
            catch (Exception ex)
            {
                _systemLogger.LogError("DataRetention_Failed", ex, _lastDataDirectory);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _retentionRunning, 0);
                _dispatcher.BeginInvoke(() => ArchiveNowCommand.RaiseCanExecuteChanged());
            }
        });
    }

    /// <summary>
    /// Is this file one the logger currently has open? The archiver asks before touching a
    /// folder — it also probes the file lock itself, but an open handle held by our own
    /// writers is worth answering directly rather than discovering through an exception.
    /// </summary>
    private bool IsFileInUseByLogger(string path)
    {
        foreach (var f in _intensityLogger.CurrentFiles)
            if (!string.IsNullOrEmpty(f) && string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) return true;
        var ratio = _ratioCsvLogger.CurrentFile;
        return !string.IsNullOrEmpty(ratio) && string.Equals(ratio, path, StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseCanExec()
    {
        ApplyAllCommand.RaiseCanExecuteChanged();
        SaveAllCommand.RaiseCanExecuteChanged();
        LoadDefaultsAllCommand.RaiseCanExecuteChanged();
        ResetExperimentCommand.RaiseCanExecuteChanged();
        ArchiveNowCommand.RaiseCanExecuteChanged();
        ChooseSimulationFileCommand.RaiseCanExecuteChanged();
        ClearSimulationFileCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        // Stop the housekeeping timer first; an in-flight pass is left to finish on its own
        // (it only touches files the logger has already released).
        _retentionTimer?.Dispose();
        _retentionTimer = null;
        AccessControl.RoleChanged       -= OnRoleChanged;
        Logger.PropertyChanged          -= OnLoggerPropertyChanged;
        _intensityLogger.StateChanged   -= OnIntensityStateChanged;
        _intensityLogger.ErrorOccurred  -= OnIntensityError;
        _intensityLogger.FilesChanged   -= OnIntensityFilesChanged;
        _intensityLogger.FileRolled     -= OnIntensityFileRolled;
        _leakMonitorEngine.AlarmStateChanged -= OnLeakAlarmStateChanged;
        _leakMonitorEngine.GoldenRunCaptured -= OnGoldenRunCaptured;
        _leakMonitorEngine.ConfigurationChanged -= OnLeakConfigChanged;
        _ratioCsvLogger.Dispose();
        WavelengthTrend.Dispose();
        _intensityLogger.Stop();
        foreach (var d in _devices)
        {
            d.PropertyChanged -= OnDevicePropertyChanged;
            d.Dispose();
        }
        Recordings.Dispose();
        RatioReview.Dispose();
        LeakMonitor.Dispose();
        LeakCalibration.Dispose();
        _leakMonitorEngine.Dispose();
        _intensityLogger.Dispose();
        LogViewer.Dispose();
        _systemLogger.Dispose();
    }

    // --- DualIntensityLogger → SystemLogger bridges ---

    private void OnIntensityStateChanged(object? sender, LoggerStateChangedEventArgs e) =>
        _systemLogger.LogIntensityLogger("StateChanged",
            $"Logger state {e.OldState} → {e.NewState}",
            related: $"From={e.OldState},To={e.NewState}",
            value: e.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private void OnIntensityError(object? sender, LoggerErrorEventArgs e) =>
        _systemLogger.LogIntensityLogger("Error", e.Message,
            value: e.Exception is null ? "" : $"Exception={e.Exception.GetType().Name}",
            severity: LogSeverity.Error);

    /// <summary>
    /// A writer continued into a new file without the save session ending. Logged with the
    /// reason because the two causes need different reactions: hitting the row cap is
    /// routine, while an axis change means an acquisition parameter was reapplied mid-save —
    /// the run's spectra are then split across files with different wavelength axes, and any
    /// Golden Run captured under the old exposure no longer applies.
    /// </summary>
    private void OnIntensityFileRolled(object? sender, LoggerFileRolledEventArgs e)
    {
        var axis = e.Reason == FileRollReason.SpectrumAxisChanged;
        _systemLogger.LogIntensityLogger(
            axis ? "SpectrumAxisChanged" : "FileRotated",
            axis
                ? "Wavelength axis changed mid-save — continued into a new CSV with the new axis. " +
                  "If this followed a parameter change, re-capture the Golden Run."
                : "Row limit reached — continued into a new CSV.",
            related: $"Device={(e.DeviceIndex < DeviceProfiles.Length ? DeviceProfiles[e.DeviceIndex].Tag : $"OES{e.DeviceIndex + 1}")}",
            value: e.NewPath,
            severity: axis ? LogSeverity.Warning : LogSeverity.Information);
    }

    private void OnIntensityFilesChanged(object? sender, EventArgs e)
    {
        var files = _intensityLogger.CurrentFiles;
        var summary = string.Join("; ", Enumerable.Range(0, files.Count)
            .Select(i => $"{(i < DeviceProfiles.Length ? DeviceProfiles[i].Tag : $"OES{i + 1}")}={files[i]}"));
        _systemLogger.LogIntensityLogger("FilesChanged",
            "Intensity logger writers opened or closed",
            value: summary);
    }

    // --- LeakMonitorEngine → SystemLogger / settings bridges ---

    private void OnLeakAlarmStateChanged(object? sender, LeakAlarmEventArgs e)
    {
        var severity = e.NewLevel switch
        {
            LeakAlarmLevel.Alarm   => LogSeverity.Error,
            LeakAlarmLevel.Warning => LogSeverity.Warning,
            _                      => LogSeverity.Information,
        };
        _systemLogger.LogSystemEvent(severity, "LeakMonitorState",
            $"Leak monitor {e.OldLevel} → {e.NewLevel}",
            related: $"From={e.OldLevel},To={e.NewLevel}",
            value: e.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void OnGoldenRunCaptured(object? sender, GoldenRun run)
    {
        PersistLeakMonitorSettings($"GoldenRun={run.Name}");
        _systemLogger.LogSystemEvent(LogSeverity.Information, "GoldenRunCaptured",
            $"Leak-monitor Golden Run baseline captured: {run.Name}",
            related: $"Ratios={run.Baselines.Count}",
            value: $"PlasmaFloor={run.PlasmaPresentFloor:G4}");
    }

    private void OnLeakConfigChanged(object? sender, EventArgs e)
    {
        PersistLeakMonitorSettings("ReferenceLineChanged");
        _systemLogger.LogSystemEvent(LogSeverity.Information, "LeakMonitorConfigChanged",
            "Leak-monitor ratio configuration changed");
    }

    /// <summary>
    /// Persists the leak-monitor section immediately — re-reads on-disk settings and swaps
    /// in only that section, so an unsaved Configuration-tab edit is not clobbered (mirrors
    /// how AccessControl edits are persisted).
    /// </summary>
    private void PersistLeakMonitorSettings(string context)
    {
        try
        {
            var onDisk = _settingsService.Load();
            onDisk.LeakMonitor = _leakMonitorEngine.Settings;
            _settingsService.Save(onDisk);
        }
        catch (Exception ex)
        {
            _systemLogger.LogError("LeakMonitor_Persist_Failed", ex, context);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
