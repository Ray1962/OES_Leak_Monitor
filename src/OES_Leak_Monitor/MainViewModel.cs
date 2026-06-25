using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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

        // Load persisted settings (or defaults) before wiring change handlers, so the
        // initial property writes don't trigger spurious CanExecute work.
        var settings = _settingsService.Load();
        ApplySettingsToDevices(settings);
        Logger.LoadFrom(settings.Logger);

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

        // Leak Calibration tab: a guided wizard that captures "ratio rise ↔ known leak rate"
        // points and fits a per-ratio sensitivity, persisted to settings.json (Engineer+).
        LeakCalibration = new LeakCalibrationViewModel(_leakMonitorEngine,
            () => PersistLeakMonitorSettings("LeakCalibrationSaved"), _systemLogger);

        // Ratio-trend CSV: a sibling of each intensity-logger save session, written
        // while the threshold logger is saving (plasma intensity above the threshold).
        _ratioCsvLogger = new RatioCsvLogger(_intensityLogger, _leakMonitorEngine, _systemLogger);

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
        ChooseSimulationFileCommand = new RelayCommand(ChooseSimulationFile, () => IsEngineerOrHigher);
        ClearSimulationFileCommand  = new RelayCommand(ClearSimulationFile,
            () => IsEngineerOrHigher && _simulation.IsLoaded);

        // Initial role is Guest → propagate the action gate so the per-device buttons
        // start out disabled until the user signs in.
        foreach (var d in _devices) d.ActionsAllowed = IsOperatorOrHigher;
        LeakMonitor.SetRole(IsOperatorOrHigher, IsEngineerOrHigher);
        RatioSetup.SetRole(IsEngineerOrHigher);
        LeakCalibration.SetRole(IsEngineerOrHigher);
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
            // Close the current Intensity (and, via FilesChanged, Ratio) save session
            // so a Stop genuinely ends the recording — the next Start gets new files.
            _intensityLogger.Stop();
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
        // (1) Roll to a fresh Intensity (and lockstep Ratio) CSV. The logger stays armed —
        // Stop() force-closes to Idle so the next threshold cross opens new files.
        _intensityLogger.Stop();

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
            SimulationCsvPath = _simulation.FilePath, // keep the Test-mode playback selection
        };
        _settingsService.Save(settings);
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

    private void RaiseCanExec()
    {
        ApplyAllCommand.RaiseCanExecuteChanged();
        SaveAllCommand.RaiseCanExecuteChanged();
        LoadDefaultsAllCommand.RaiseCanExecuteChanged();
        ResetExperimentCommand.RaiseCanExecuteChanged();
        ChooseSimulationFileCommand.RaiseCanExecuteChanged();
        ClearSimulationFileCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        AccessControl.RoleChanged       -= OnRoleChanged;
        _intensityLogger.StateChanged   -= OnIntensityStateChanged;
        _intensityLogger.ErrorOccurred  -= OnIntensityError;
        _intensityLogger.FilesChanged   -= OnIntensityFilesChanged;
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
