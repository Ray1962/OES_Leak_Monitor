using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Aqst.OesSpectrometer.Models;
using OxyPlot;

namespace OES_Leak_Monitor;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    // The one and only place the app folder name is spelled out — everything else
    // takes the resolved paths from this instance.
    private readonly OesAppPaths _paths = new("OES_Leak_Monitor");

    /// <summary>Day folder the last recording opened in — where the configuration snapshot and
    /// the system-log copies go. See <see cref="MirrorRunContext"/>.</summary>
    private string? _lastDayFolder;
    private readonly SettingsService _settingsService;
    private readonly SystemLogger _systemLogger;
    private readonly DualIntensityLogger _intensityLogger;
    private readonly LeakMonitorEngine _leakMonitorEngine;
    private readonly RatioCsvLogger _ratioCsvLogger;
    private readonly SecsBridge _secs;
    private readonly List<DeviceViewModel> _devices;

    // Whether the spectrometer has ever been connected this run. "Connection lost" is a
    // transition, not a state: at start-up nothing is connected and that is not a fault.
    private bool _deviceWasConnected;
    private bool _deviceInError;

    // Test-mode replay of a recorded plasma spectrum (Replay tab): while it runs, it replaces
    // the device's synthetic frames with the recording's own — on the recording's wavelength
    // axis and at its own frame intervals — so the leak-monitor algorithms can be validated
    // against real data with no spectrometer attached. A no-op for real-hardware frames.
    private readonly SpectrumReplaySource _replay = new();

    // Set by the spectrum mapper on every device tick: true while the replay is driving that
    // slot's stream, in which case the mapper has already fanned the replayed frames out and
    // the device's own frame must not be forwarded again. Written and read on the acquisition
    // thread within one frame's call chain (mapper first, then SpectrumAvailable) — per slot,
    // so a second device's frames could never clear the flag between the two.
    private bool[] _replayHandledFrame = Array.Empty<bool>();

    // True while the recorders are pointed at replay output (SIM file prefix), so a replayed
    // run can never be mistaken for a real one in the Recordings / Ratio Review lists.
    private bool _replayOutputActive;

    // One teardown at a time when hardware frames arrive during a replay — see
    // AbortReplayForHardware. Set on the acquisition thread, cleared on the UI thread.
    private volatile bool _replayAbortPending;

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

        // Built first, before anything that can raise an event at it. The device view-models
        // below report connection and error transitions into it, and a half-constructed host is
        // the classic way that becomes a null reference on someone else's machine. Nothing is
        // opened here — Configure() at the end of the constructor decides that, once the
        // settings have been read. The acquisition lambda is not called until then either.
        _secs = new SecsBridge(_systemLogger, _paths.ConfigDirectory, _paths.LogDirectory,
            // The device list is built below, so the lambda checks rather than assumes: a host
            // querying before there is a device gets zeros, not an exception on a socket thread.
            () => _devices is { Count: > 0 } devices
                ? new SecsBridge.AcquisitionInfo(devices[0].IntegrationTimeMs, devices[0].AverageCount)
                : default);

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
        // The site's own emission lines join the fixed catalog before anything reads it — the
        // Ratio Setup pickers snapshot it at construction, and a ratio saved against a user
        // line has to find that line again or the picker will match it to something else.
        SpectralLineCatalog.SetUserLines(settings.LeakMonitor.UserSpectralLines?
            .Select(l => new SpectralLine(l.Species, l.WavelengthNm)));
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
        // The line view's wavelength set is a review preference, so it is restored and then
        // persisted as it changes rather than waiting for the Configuration tab's Save — the
        // person comparing recordings is not on that tab and has no reason to visit it.
        Recordings.RestoreTrendWavelengths(settings.RecordingsWavelengths);
        Recordings.TrendWavelengthsChanged += (_, _) => PersistRecordingsWavelengths();
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

        // Restore the last-used replay recording (if it still exists on disk). Loading it does
        // not start it — the Replay tab's Play does. A missing/unparseable file leaves test
        // mode on the built-in synthetic generator.
        _replay.Speed = settings.ReplaySpeed;
        if (!string.IsNullOrWhiteSpace(settings.SimulationCsvPath) && File.Exists(settings.SimulationCsvPath))
        {
            try { _replay.Load(settings.SimulationCsvPath); }
            catch (Exception ex) { _systemLogger.LogError("Replay_Load_Failed", ex, settings.SimulationCsvPath); }
        }

        // Monitor tab: a live intensity time-trend at the "selected" wavelength — i.e. the
        // trigger (threshold) wavelength configured in the LoggerPanel — plus the first few
        // monitored wavelengths logged into the intensity CSV. The chart follows that config:
        // seeded here and re-pointed by ApplyAll whenever it is applied.
        var loggerSettings = Logger.ToSettings();
        WavelengthTrend = new WavelengthTrendViewModel(
            loggerSettings.TriggerWavelength, TrendThresholdFor(loggerSettings),
            loggerSettings.MonitoredWavelengths?.Select(w => (double)w));
        // Both live trend charts share one retention setting; AppSettings has already clamped it.
        _trendRetentionMinutes = settings.TrendRetentionMinutes;
        WavelengthTrend.SetRetention(TimeSpan.FromMinutes(_trendRetentionMinutes));

        // Actinometry leak monitor: build from persisted config, feed it the same
        // spectrum stream the intensity logger sees, and bridge its lifecycle into
        // the system log. Golden Run captures are persisted as they happen.
        _leakMonitorEngine = new LeakMonitorEngine(settings.LeakMonitor, _systemLogger);
        // Absolute-intensity ratios take their plasma-present gate from the logger's save
        // trigger (same quantity, same threshold), so the engine has to be told what that is —
        // here and again on every ApplyAll. Ratio-mode entries still gate on their reference.
        _leakMonitorEngine.ConfigureTrigger(loggerSettings);
        // Golden Runs record the acquisition conditions they were captured under, so a later
        // exposure / averaging change is reported instead of silently re-scaling every
        // absolute-intensity reading against a baseline that no longer applies.
        _leakMonitorEngine.ConfigureAcquisition(_devices[0].ToSettings());
        LeakMonitor = new LeakMonitorViewModel(_leakMonitorEngine, _systemLogger);
        LeakMonitor.SetRetention(TimeSpan.FromMinutes(_trendRetentionMinutes));

        // Single fan-out. Two hooks per device, in this order on every frame:
        //   1. SpectrumMapper  — replaces the frame while a replay is running, and delivers
        //      the replayed frames itself (a fast-forwarding tick owes the consumers several
        //      frames, but the device asks for exactly one to draw).
        //   2. SpectrumAvailable — the normal path when no replay is running.
        // The mapper runs before the plot and the event, so a replayed frame reaches the
        // DevicePanel's own live spectrum plot too — which is why the substitution can carry
        // the recording's wavelength axis instead of being squeezed onto the synthetic one.
        _replayHandledFrame = new bool[_devices.Count];
        for (int i = 0; i < _devices.Count; i++)
        {
            int slot = i;
            _devices[i].SpectrumMapper = raw => MapDeviceFrame(slot, raw);
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

        // Fills the logger's two wavelength fields from the emission-line catalog — the
        // LoggerPanel comes from the framework package and knows nothing about emission lines,
        // so the picker sits beside it and writes into the same properties a hand-typed value
        // would. It owns no state of its own; the LoggerPanel's boxes remain the value.
        LinePicker = new LinePickerViewModel(Logger, _leakMonitorEngine);

        // Wavelength Calibration tab: a staged editor for the catalog-level wavelength-drift
        // correction overlay. Like a Ratio Setup edit it is persisted immediately but only
        // applied to the engine when acquisition (re)starts (via ReloadRatios).
        WavelengthCorrection = new WavelengthCorrectionViewModel(_leakMonitorEngine,
            () => PersistLeakMonitorSettings("WavelengthCorrectionsSaved"), _systemLogger);
        // The Ratio Setup line pickers annotate each line with its offset, so refresh them
        // when the overlay changes — without reloading rows, which would drop unsaved edits.
        WavelengthCorrection.CorrectionsSaved += (_, _) => RatioSetup.RefreshCorrections();
        // A saved user line is usable at once — the catalog is not something the running engine
        // computes with, so unlike a correction it needs no acquisition restart. Push it into
        // the Ratio Setup pickers rather than making the operator find that out by restarting.
        WavelengthCorrection.LineTable.UserLinesSaved += (_, _) =>
        {
            RatioSetup.RefreshLineCatalog();
            LinePicker.RefreshCatalog();
            Recordings.RefreshLineCatalog();
        };
        WavelengthCorrection.CorrectionsSaved += (_, _) => LinePicker.RefreshCatalog();

        // Leak Calibration tab: a guided wizard that captures "ratio rise ↔ known leak rate"
        // points and fits a per-ratio sensitivity, persisted to settings.json (Engineer+).
        LeakCalibration = new LeakCalibrationViewModel(_leakMonitorEngine,
            () => PersistLeakMonitorSettings("LeakCalibrationSaved"), _systemLogger);
        // Constructed after the logger has been hydrated, for the same reason the review tabs
        // are: it reads the data directory and the save trigger out of it, and against the
        // factory defaults it would list the AppData fallback folder and gate on a threshold
        // nobody configured.
        BaselineBuilder = new BaselineBuilderViewModel(_leakMonitorEngine, Logger,
            _paths.DataDirectory, _systemLogger);

        // Ratio-trend CSV: its own recorder, running for as long as the OES acquires —
        // deliberately NOT tied to the threshold logger's save sessions, so disarming the
        // raw-spectrum recorder (or running below its trigger threshold) no longer throws
        // away the leak history. It only shares the output folder and file prefix.
        _ratioCsvLogger = new RatioCsvLogger(_leakMonitorEngine, _paths.DataDirectory, _systemLogger);
        _ratioCsvLogger.Configure(loggerSettings);

        // Replay tab: play a recorded full-spectrum CSV through the live pipeline to validate
        // the leak-monitor algorithms without hardware. It owns the transport; this class owns
        // what the replay does to the recorders and the alarm gate.
        Replay = new ReplayViewModel(_replay, _leakMonitorEngine.Settings,
            () => PersistLeakMonitorSettings("ReplayAlarmGate"),
            () => _lastDataDirectory, _systemLogger);
        Replay.ReplayStarting += (_, _) => BeginReplayOutput();
        Replay.ReplayStopped  += (_, _) => EndReplayOutput(finished: false);
        Replay.FileChanged    += (_, path) => PersistReplaySelection(path);
        Replay.PropertyChanged += OnReplayPropertyChanged;
        // Reaching the end of the recording is handled in MapDeviceFrame, after that tick's
        // frames have been delivered — see ReplayTick.Finished.
        UpdateTestModeContext();
        UpdateReplayAvailability();   // a device may already be connected when settings reload

        // SECS/GEM equipment interface (the object itself was built at the top of this
        // constructor). It reads the leak monitor's snapshots and reports them to a fab host;
        // nothing it does feeds back into the measurement. Off unless settings.json says
        // otherwise, so an upgrade opens no port until someone asks for one.
        _leakMonitorEngine.SampleProcessed += (_, snap) => _secs.OnSample(snap);
        _leakMonitorEngine.Acknowledged += (_, e) => _secs.OnAcknowledged(e);
        Secs = new SecsViewModel(_secs, settings.Secs, PersistSecsSettings);
        _secs.Configure(settings.Secs);

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

        // Initial role is Guest → propagate the action gate so the per-device buttons
        // start out disabled until the user signs in.
        foreach (var d in _devices) d.ActionsAllowed = IsOperatorOrHigher;
        LeakMonitor.SetRole(IsOperatorOrHigher, IsEngineerOrHigher, AccessControl.CurrentUsername);
        RatioSetup.SetRole(IsEngineerOrHigher);
        WavelengthCorrection.SetRole(IsEngineerOrHigher);
        LeakCalibration.SetRole(IsEngineerOrHigher);
        BaselineBuilder.SetRole(IsEngineerOrHigher);
        Replay.SetRole(IsEngineerOrHigher);
        Secs.SetRole(IsEngineerOrHigher);

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
        // Whether a real spectrometer is attached decides whether the Replay tab can do
        // anything at all, so it is tracked on both properties that can change it.
        if (e.PropertyName is nameof(DeviceViewModel.IsConnected)
                           or nameof(DeviceViewModel.IsTestMode))
            UpdateReplayAvailability();

        if (e.PropertyName is nameof(DeviceViewModel.IsConnected)
                           or nameof(DeviceViewModel.Status))
            ReportDeviceHealthToSecs();

        if (e.PropertyName != nameof(DeviceViewModel.IsAcquiring)) return;
        if (sender is not DeviceViewModel d) return;

        bool now = d.IsAcquiring;
        if (now != _wasAcquiring) _secs.OnAcquisitionChanged(now);
        if (now && !_wasAcquiring)
        {
            _leakMonitorEngine.ReloadRatios();
            _systemLogger.LogSystemEvent(LogSeverity.Information, "LeakMonitorRatiosApplied",
                "Species-ratio configuration (re)applied on OES acquisition start");
        }
        else if (!now && _wasAcquiring)
        {
            // A replay is fed by the device's ticks, so stopping acquisition stops it dead.
            // End it properly rather than leaving the tab claiming to be playing.
            if (_replay.IsActive)
            {
                _replay.Stop();
                EndReplayOutput(finished: false);
            }
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
    /// Tells the Replay tab whether a real spectrometer is attached. Replay substitutes only
    /// test-mode frames, so with hardware connected Play can deliver nothing — it is refused
    /// with a reason rather than left to look as though it is running.
    /// </summary>
    private void UpdateReplayAvailability() =>
        Replay.SetHardwareAttached(_devices.Any(d => d.IsConnected && !d.IsTestMode));

    /// <summary>
    /// Maps the spectrometer's state onto the two equipment-fault alarms a host cares about
    /// (ALID 012 / 013).
    /// <para>
    /// "Connection lost" is deliberately a <b>transition</b>, not a state: at start-up nothing
    /// is connected, and an alarm the tool raises about itself every time it launches is one
    /// the host learns to ignore. A deliberate Disconnect raises it too — from the fab's side
    /// an OES that has stopped watching the chamber is the same fact either way, and the alarm
    /// clears the moment it comes back.
    /// </para>
    /// </summary>
    private void ReportDeviceHealthToSecs()
    {
        var device = _devices[0];

        bool connected = device.IsConnected;
        if (connected)
        {
            _deviceWasConnected = true;
            _secs.ReportFault(SecsFault.ConnectionLost, set: false);
        }
        else if (_deviceWasConnected)
        {
            _secs.ReportFault(SecsFault.ConnectionLost, set: true, detail: device.StatusText);
        }

        bool error = device.Status == Aqst.OesSpectrometer.Models.DeviceConnectionStatus.Error;
        if (error != _deviceInError)
        {
            _deviceInError = error;
            _secs.ReportFault(SecsFault.AcquisitionError, error, device.StatusMessage ?? device.StatusText);
        }
    }

    /// <summary>
    /// Frame substitution, called by the device before it plots the frame or raises
    /// <see cref="DeviceViewModel.SpectrumAvailable"/>. While a replay is running it delivers
    /// every recorded frame that has come due to the consumers itself — fast-forward means one
    /// device tick can owe them several — and returns the newest one for the live plot. With no
    /// replay running the frame is passed through untouched and the normal
    /// <see cref="OnDeviceSpectrum"/> path fans it out. Runs on the acquisition thread.
    /// </summary>
    private SpectrumSample MapDeviceFrame(int slot, SpectrumSample raw)
    {
        // A hardware frame always wins — SpectrumReplaySource refuses to substitute one. If the
        // recorders are pointed at replay output when such a frame arrives, the replay cannot
        // deliver anything and the SIM prefix is now filing *measurement* as replay output,
        // which is the safeguard used exactly backwards. Tear it down on the first such frame.
        if (!raw.IsTestMode && _replayOutputActive) AbortReplayForHardware();

        var tick = _replay.Advance(raw);
        if ((uint)slot < (uint)_replayHandledFrame.Length) _replayHandledFrame[slot] = tick.Handled;
        if (!tick.Handled) return raw;

        for (int i = 0; i < tick.Frames.Count; i++)
            FanOutSpectrum(slot, tick.Frames[i]);

        // Only now that the last frames have reached the recorders is it safe to tear the
        // replay's session down — closing first left them to open a session of their own.
        if (tick.Finished)
            _dispatcher.BeginInvoke(() =>
            {
                EndReplayOutput(finished: true);
                Replay.NotifyFinished();
            });

        // Paused, or the device ticking faster than the recording: hold the last replayed
        // frame on the plot rather than letting the synthetic spectrum flash back on screen.
        return tick.Display ?? raw;
    }

    /// <summary>
    /// The single fan-out for every device spectrum frame. Skipped while a replay is running,
    /// because <see cref="MapDeviceFrame"/> has already delivered that tick's frames — the
    /// device's own synthetic frame must not be added on top. Runs on the acquisition thread.
    /// </summary>
    private void OnDeviceSpectrum(int slot, SpectrumSample sample)
    {
        if ((uint)slot < (uint)_replayHandledFrame.Length && _replayHandledFrame[slot]) return;
        FanOutSpectrum(slot, sample);
    }

    /// <summary>Hands one effective frame to the intensity logger, the leak engine, and the
    /// Monitor-tab trend, so all three always see the same spectrum.</summary>
    private void FanOutSpectrum(int slot, SpectrumSample sample)
    {
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
        LeakMonitor.SetRole(IsOperatorOrHigher, IsEngineerOrHigher, AccessControl.CurrentUsername);
        RatioSetup.SetRole(IsEngineerOrHigher);
        WavelengthCorrection.SetRole(IsEngineerOrHigher);
        LeakCalibration.SetRole(IsEngineerOrHigher);
        BaselineBuilder.SetRole(IsEngineerOrHigher);
        Replay.SetRole(IsEngineerOrHigher);
        Secs.SetRole(IsEngineerOrHigher);

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
        // Both recorders take the logger's folder and prefix — an edited path takes effect on
        // their next session, not mid-file. This also re-imposes the replay marker on the
        // intensity logger, which the Apply above just configured with the unmarked prefix.
        ConfigureRecorderOutput();
        // The absolute-intensity plasma gate follows the same trigger the logger just took, so
        // the gate and the recorder can't disagree for the rest of the run. Hot-applied on
        // purpose: it is a logger setting, not part of the staged ratio configuration.
        _leakMonitorEngine.ConfigureTrigger(ls);
        // Device parameters were just pushed above — refresh the fingerprint Golden Runs are
        // stamped with and compared against.
        _leakMonitorEngine.ConfigureAcquisition(_devices[0].ToSettings());
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
        WavelengthTrend.Configure(ls.TriggerWavelength, TrendThresholdFor(ls),
            ls.MonitoredWavelengths?.Select(w => (double)w));
        // Both trend charts share one retention window — see AppSettings.TrendRetentionMinutes.
        var trendRetention = TimeSpan.FromMinutes(TrendRetentionMinutes);
        WavelengthTrend.SetRetention(trendRetention);
        LeakMonitor.SetRetention(trendRetention);
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
        foreach (var d in _devices)
        {
            d.LoadDefaultsCommand.Execute(null);
            // Same reason as the logger lines below: the framework's device defaults are a
            // demo's (ForceTestMode on), and Load Defaults has to land where a fresh install
            // lands. Left alone it silently re-arms test mode, and the next Save persists it —
            // a tool that quietly stops measuring and keeps writing files that look real.
            d.ForceTestMode = AppSettings.DefaultForceTestMode;
        }
        Logger.LoadDefaults();
        // LoadDefaults resets the logger to the framework's values, which are a general-purpose
        // recorder's, not this app's: disarmed, and triggering on 387 nm. "Load Defaults" has to
        // land where a fresh install lands, or the two meanings of "default" disagree.
        Logger.Enabled = AppSettings.DefaultLoggerEnabled;
        Logger.TriggerWavelength = AppSettings.DefaultTriggerWavelengthNm;
        TrendRetentionMinutes = AppSettings.DefaultTrendRetentionMinutes;
        GoldenRunCaptureSeconds = LeakMonitorSettings.DefaultGoldenRunCaptureSeconds;
        StatusMessage = "Defaults loaded — click Apply to push to devices and logger.";
        _systemLogger.LogSystemEvent(LogSeverity.Information, "LoadDefaultsAll",
            "Reset to factory defaults (not yet applied/persisted)",
            related: $"User={AccessControl.CurrentUsername ?? "(guest)"}");
    }

    // --- replay output -------------------------------------------------------------------
    //
    // A replay drives the same recorders a real run does — deliberately, since a replayed run
    // has to be reviewable in the Recordings / Ratio Review tabs to be worth anything. What
    // marks it is the file prefix: while a replay runs, both recorders write SIM-prefixed
    // files, so replayed data is never mistaken for measurement in today's data folder.

    /// <summary>Prefix a replayed run's files carry. Kept in front of the configured prefix so
    /// the filename still parses as <c>{prefix}_{tag}_{MMddHHmmss}</c> and the review tabs list
    /// it as usual.</summary>
    private const string ReplayPrefixMarker = "SIM";

    /// <summary>
    /// Switch the recorders over to replay output. Both are force-closed first: the prefix is
    /// captured when a session opens, so a run that began before the replay would otherwise
    /// keep writing real-looking rows into a real-looking file for the whole replay.
    /// </summary>
    private void BeginReplayOutput()
    {
        _intensityLogger.Stop();
        _ratioCsvLogger.Stop();
        _replayOutputActive = true;
        ConfigureRecorderOutput();
        UpdateTestModeContext();

        StatusMessage = $"Replay running — recorders are writing {ReplayPrefixMarker}-prefixed files.";
        _systemLogger.LogSystemEvent(LogSeverity.Information, "ReplayStarted",
            "Recorded-spectrum replay started; recorders switched to replay output",
            related: $"User={AccessControl.CurrentUsername ?? "(guest)"}",
            value: $"Path={_replay.FilePath},Frames={_replay.FrameCount}");
    }

    /// <summary>
    /// Stops a replay that a real-hardware frame has just made impossible, and hands the
    /// recorders back. Runs on the acquisition thread, so the work is marshalled; the flag stops
    /// every subsequent frame queuing another teardown while the first is still pending.
    /// </summary>
    private void AbortReplayForHardware()
    {
        if (_replayAbortPending) return;
        _replayAbortPending = true;
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!_replayOutputActive) return;
                _replay.Stop();
                EndReplayOutput(finished: false);
                StatusMessage = "Replay stopped — a spectrometer is connected, and hardware " +
                                "frames are never replaced.";
                _systemLogger.LogSystemEvent(LogSeverity.Warning, "ReplayAbortedByHardware",
                    "Replay stopped because real-hardware frames arrived: replay only substitutes " +
                    "test-mode frames, so nothing was being replayed, and the recorders were " +
                    "writing measured data under the replay prefix. Recorders are back on the " +
                    "normal prefix. Disconnect the spectrometer, or connect with Force test mode, " +
                    "to replay a recording.",
                    value: $"Path={_replay.FilePath}");
            }
            finally { _replayAbortPending = false; }
        });
    }

    /// <summary>
    /// Hand the recorders back to normal output. Called when the operator stops playback and
    /// when the recording runs out, closing the replay's files there and then rather than
    /// leaving them open for whatever arrives next.
    /// </summary>
    private void EndReplayOutput(bool finished)
    {
        if (!_replayOutputActive) return;
        // Drop the monitors' live state *before* the recorders are closed and the prefix
        // restored. The replay's last frames leave an EMA, a confirmation timer and possibly a
        // latched alarm behind; the first synthetic frame after teardown then opens a session
        // under the ordinary prefix and writes that state into it. A real run did exactly that:
        // P_Ratio_0820153410.csv, three quarters of a minute after the recording ended, whose
        // first row carries the replay's own σ-scores and OverallState=Alarm — a production
        // filename holding replayed data, and an audit trail showing an alarm nobody caused.
        // Clearing the latch is the deliberate half of this: an alarm confirmed against a
        // recording is not a fact about the chamber, and carrying it into live monitoring would
        // ask an operator to acknowledge something that never happened here. It is logged.
        _leakMonitorEngine.ResetRuntimeState(clearAlarms: true,
            reason: "recorded-spectrum replay ended; alarms confirmed against the recording " +
                    "do not carry into live monitoring");
        _intensityLogger.Stop();
        _ratioCsvLogger.Stop();
        _replayOutputActive = false;
        ConfigureRecorderOutput();
        // Close again: the acquisition thread can deliver a frame between the two calls above,
        // which would open a session that then sits there — with whichever prefix was current
        // — holding a row nobody asked for. Cheap, and it makes the end of a replay a clean
        // edge whatever the timing.
        _intensityLogger.Stop();
        _ratioCsvLogger.Stop();
        UpdateTestModeContext();

        StatusMessage = finished
            ? "Replay finished — the whole recording has been played through."
            : "Replay stopped.";
        _systemLogger.LogSystemEvent(LogSeverity.Information, "ReplayEnded",
            finished
                ? "Recorded-spectrum replay reached the end of the file; recorders closed"
                : "Recorded-spectrum replay stopped by the operator; recorders closed",
            value: $"Path={_replay.FilePath}");
    }

    /// <summary>
    /// Point both recorders at the logger's current output settings, with the replay marker
    /// folded into the file prefix while a replay is running. Called whenever either side can
    /// have changed: replay start/stop, and every Apply (which pushes the unmarked prefix
    /// straight into the intensity logger).
    /// </summary>
    private void ConfigureRecorderOutput()
    {
        var ls = Logger.ToSettings();
        if (_replayOutputActive && !ls.FilePrefix.StartsWith(ReplayPrefixMarker, StringComparison.OrdinalIgnoreCase))
            ls.FilePrefix = ReplayPrefixMarker + ls.FilePrefix;
        _intensityLogger.Configure(ls);
        _ratioCsvLogger.Configure(ls);
    }

    private void OnReplayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReplayViewModel.AlarmsEnabledInTestMode))
            UpdateTestModeContext();
    }

    /// <summary>
    /// Keeps the Leak Monitor tab's test-mode banner honest: what the spectra actually are
    /// (a recording or the synthetic generator) and whether alarms are being raised for them.
    /// A banner that always reads "synthetic spectra; alarms are suppressed" is wrong in both
    /// halves during a replay with the alarm gate open.
    /// </summary>
    private void UpdateTestModeContext() =>
        LeakMonitor.SetTestModeContext(
            _replayOutputActive
                ? $"replaying {Path.GetFileName(_replay.FilePath) ?? "a recording"}"
                : "synthetic spectra",
            _leakMonitorEngine.Settings.SuppressAlarmsInTestMode);

    /// <summary>
    /// Persists the replay selection (file + speed) immediately — re-reads on-disk settings and
    /// swaps in only those fields, so an unsaved Configuration-tab edit is not clobbered
    /// (mirrors how AccessControl and leak-monitor edits are persisted).
    /// </summary>
    private void PersistReplaySelection(string? path)
    {
        try
        {
            var onDisk = _settingsService.Load();
            onDisk.SimulationCsvPath = path;
            onDisk.ReplaySpeed = _replay.Speed;
            _settingsService.Save(onDisk);
        }
        catch (Exception ex)
        {
            _systemLogger.LogError("ReplaySelection_Persist_Failed", ex, path ?? "(none)");
        }
    }

    /// <summary>
    /// Persists the SECS tab's settings and rebuilds the interface with them. Same
    /// read-modify-write idiom as the replay selection, so saving on the SECS tab does not
    /// clobber an unsaved Configuration-tab edit.
    /// <para>
    /// The rebuild happens after the write, not before: what is running should always be what
    /// is on disk, so a restart of the app does not quietly change the tool's behaviour.
    /// </para>
    /// </summary>
    private void PersistSecsSettings(SecsSettings secs)
    {
        try
        {
            var onDisk = _settingsService.Load();
            onDisk.Secs = secs;
            _settingsService.Save(onDisk);
            _systemLogger.LogSystemEvent(LogSeverity.Information, "SecsSettingsSaved",
                "SECS interface settings saved",
                related: $"User={AccessControl.CurrentUsername ?? "(guest)"},Enabled={secs.Enabled}," +
                         $"Chamber={secs.ChamberCode:00}",
                value: $"{secs.IpAddress}:{secs.Port} deviceId={secs.DeviceId}");
        }
        catch (Exception ex)
        {
            _systemLogger.LogError("SecsSettings_Persist_Failed", ex, "");
        }
        _secs.Configure(secs);
    }

    /// <summary>Persists the Recordings line view's wavelength set, same read-modify-write
    /// idiom as the replay selection so no unsaved Configuration edit is clobbered.</summary>
    private void PersistRecordingsWavelengths()
    {
        try
        {
            var onDisk = _settingsService.Load();
            onDisk.RecordingsWavelengths = Recordings.TrendWavelengthValues.ToList();
            _settingsService.Save(onDisk);
        }
        catch (Exception ex)
        {
            _systemLogger.LogError("RecordingsWavelengths_Persist_Failed", ex, "");
        }
    }

    /// <summary>
    /// Persist all devices' parameters and the shared logger settings as one JSON
    /// file. Backs the unified Save button at the bottom of the Configuration tab.
    /// </summary>
    /// <summary>
    /// Everything the tabs currently hold, as the object that would be written to
    /// <c>settings.json</c>. One definition, because the snapshot kept beside the data
    /// (<see cref="ConfigSnapshot"/>) has to be the same settings the Save button would write —
    /// a second gathering of them would eventually disagree, and the snapshot exists precisely
    /// to be trusted months later.
    /// </summary>
    private AppSettings CurrentSettings() => new()
    {
        Devices       = _devices.Select(d => d.ToSettings()).ToList(),
        Logger        = Logger.ToSettings(),
        LeakMonitor   = _leakMonitorEngine.Settings, // includes captured Golden Runs
        AccessControl = AccessControl.SnapshotConfig(), // preserve user list across saves
        DataRetention = _retention,
        TrendRetentionMinutes = TrendRetentionMinutes,
        SimulationCsvPath = _replay.FilePath, // keep the Replay tab's recording selection
        ReplaySpeed = _replay.Speed,
        Secs = Secs.ToSettings(),   // the SECS tab has its own Save; this preserves it here
    };

    private void SaveSettings()
    {
        var settings = CurrentSettings();
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

    /// <summary>Baseline Builder tab — a Golden Run pooled from recordings already on disk.</summary>
    public BaselineBuilderViewModel BaselineBuilder { get; }
    public WavelengthTrendViewModel WavelengthTrend { get; }
    public ReplayViewModel Replay { get; }

    /// <summary>SECS tab: interface status (any role) and its settings (Engineer+).</summary>
    public SecsViewModel Secs { get; }

    /// <summary>Emission-line picker for the logger's two wavelength fields (Configuration tab).</summary>
    public LinePickerViewModel LinePicker { get; }

    public RelayCommand ApplyAllCommand { get; }
    public RelayCommand SaveAllCommand { get; }
    public RelayCommand LoadDefaultsAllCommand { get; }
    public RelayCommand ResetExperimentCommand { get; }

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
            case nameof(LoggerViewModel.TriggerMode):
            case nameof(LoggerViewModel.TriggerPercentile):
                UpdateRecorderStatus();
                break;
        }
    }

    /// <summary>
    /// The dashed threshold line on the Monitor trend marks the save threshold against the
    /// trigger wavelength's trace. That reading is only the trigger in Wavelength mode — in
    /// the whole-frame modes the threshold applies to a different quantity entirely, so the
    /// line would sit at a meaningless height. Zero hides it.
    /// </summary>
    private static double TrendThresholdFor(LoggerSettings s) =>
        s.TriggerMode == TriggerMode.Wavelength ? s.SaveStartThresholdIntensity : 0;

    private void UpdateRecorderStatus()
    {
        var file = Logger.CurrentFile1;
        var hasFile = !string.IsNullOrWhiteSpace(file);
        var trigger = Logger.TriggerMode switch
        {
            TriggerMode.SpectrumPercentile =>
                $"the frame's {Logger.TriggerPercentile:0.#}th percentile above {Logger.SaveStartThresholdIntensity:0.#}",
            TriggerMode.AnyMonitoredWavelength =>
                $"any monitored wavelength above {Logger.SaveStartThresholdIntensity:0.#}",
            _ => $"{Logger.TriggerWavelength:0.#} nm above {Logger.SaveStartThresholdIntensity:0.#}",
        };

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

    private int _trendRetentionMinutes = AppSettings.DefaultTrendRetentionMinutes;
    /// <summary>
    /// How much history both live trend charts keep — the Monitor tab's wavelength trend and the
    /// Leak Monitor's % -of-baseline chart. Takes effect on Apply (like every other Configuration
    /// field) and is persisted by Save. Clamped here as well as on load, so a typed value out of
    /// range snaps back in the box instead of being silently ignored.
    /// </summary>
    public int TrendRetentionMinutes
    {
        get => _trendRetentionMinutes;
        set
        {
            int clamped = Math.Clamp(value, AppSettings.MinTrendRetentionMinutes,
                                            AppSettings.MaxTrendRetentionMinutes);
            // Notify even when the stored value is unchanged, so a rejected entry snaps the
            // bound TextBox back — same idiom as RatioEditViewModel.MinSnr.
            if (!Set(ref _trendRetentionMinutes, clamped) && clamped != value)
                OnPropertyChanged();
        }
    }

    /// <summary>
    /// How long a Golden Run capture averages, seconds. Lives here rather than only in
    /// settings.json because 60 s suits a steady recipe and not a short or a drifting one, and
    /// the only way to change it was to hand-edit the file — which meant nobody did. Written
    /// straight into the live leak-monitor settings (so the next capture dialog offers it) and
    /// persisted by Save; clamped here so a typed value out of range snaps back in the box.
    /// </summary>
    public double GoldenRunCaptureSeconds
    {
        get => _leakMonitorEngine.Settings.GoldenRunCaptureSeconds;
        set
        {
            double clamped = Math.Clamp(value, LeakMonitorSettings.MinGoldenRunCaptureSeconds,
                                               LeakMonitorSettings.MaxGoldenRunCaptureSeconds);
            if (_leakMonitorEngine.Settings.GoldenRunCaptureSeconds != clamped)
                _leakMonitorEngine.Settings.GoldenRunCaptureSeconds = clamped;
            // Notify unconditionally: a rejected entry has to snap the bound TextBox back.
            OnPropertyChanged();
        }
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
    }

    public void Dispose()
    {
        // Last refresh of the day's log copy: every earlier one was taken while the file was
        // still being written, so this is the only complete one the day folder will get.
        if (_lastDayFolder is not null)
        {
            try { SyncSystemLog(_lastDayFolder, DateTime.Now); }
            catch { /* shutting down; a missing log copy must not hold the window open */ }
        }

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
        Replay.PropertyChanged -= OnReplayPropertyChanged;
        Replay.Dispose();
        _ratioCsvLogger.Dispose();
        WavelengthTrend.Dispose();
        _intensityLogger.Stop();
        foreach (var d in _devices)
        {
            d.PropertyChanged -= OnDevicePropertyChanged;
            d.SpectrumMapper = null;
            d.Dispose();
        }
        _secs.Dispose();
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

    private void OnIntensityError(object? sender, LoggerErrorEventArgs e)
    {
        _systemLogger.LogIntensityLogger("Error", e.Message,
            value: e.Exception is null ? "" : $"Exception={e.Exception.GetType().Name}",
            severity: LogSeverity.Error);
        // A tool that is measuring but not recording looks healthy from the host's side, and
        // the gap is only discovered when someone goes looking for the data. Cleared when a
        // writer next opens successfully (OnIntensityFilesChanged).
        _secs.ReportFault(SecsFault.DataWriteFailure, set: true, detail: e.Message);
    }

    /// <summary>
    /// A writer continued into a new file without the save session ending. Logged with the
    /// reason because the two causes need different reactions: hitting the row cap is
    /// routine, while an axis change means an acquisition parameter was reapplied mid-save —
    /// the run's spectra are then split across files with different wavelength axes, and any
    /// Golden Run captured under the old exposure no longer applies.
    /// </summary>
    /// <summary>
    /// Keeps the day folder self-contained: a redacted copy of the configuration and of the day's
    /// system log, beside the recordings they describe.
    ///
    /// <para>The data folder is wherever the operator pointed the logger; everything needed to
    /// read what is in it — the save threshold that decided which frames were evaluated at all,
    /// the ratio definitions, the corrections, and the log saying what the tool did — lives under
    /// <c>%AppData%</c>. Copying a day folder off the machine takes the first and leaves the
    /// second. That is not a mistake anyone makes once: it is what the folder structure invites,
    /// so the fix belongs here rather than in a reminder.</para>
    ///
    /// <para>Best-effort throughout. Both are conveniences beside the measurement, and a failure
    /// to write one must never disturb the recording that prompted it.</para>
    /// </summary>
    private void MirrorRunContext(string? dayFolder)
    {
        if (string.IsNullOrWhiteSpace(dayFolder)) return;
        _lastDayFolder = dayFolder;
        var now = DateTime.Now;

        var written = ConfigSnapshot.TryWrite(dayFolder, CurrentSettings(), now, out var configError);
        if (configError is not null)
            _systemLogger.LogSystemEvent(LogSeverity.Warning, "ConfigSnapshotFailed",
                $"Could not write the configuration snapshot into {dayFolder}: {configError}. " +
                "The recordings are unaffected, but a copy of this folder will not carry the " +
                "settings needed to interpret them.",
                value: dayFolder);
        else if (written is not null)
            _systemLogger.LogSystemEvent(LogSeverity.Information, "ConfigSnapshotWritten",
                "Configuration snapshot written beside the recordings (access-control section " +
                "removed).", value: written);

        SyncSystemLog(dayFolder, now);
    }

    private void SyncSystemLog(string dayFolder, DateTime day)
    {
        int copied = SystemLogMirror.Sync(dayFolder, _paths.LogDirectory, day, out var errors);
        if (errors.Count > 0)
            _systemLogger.LogSystemEvent(LogSeverity.Warning, "SystemLogMirrorFailed",
                $"Could not copy {errors.Count} system-log file(s) into {dayFolder}. A copy of " +
                "this folder will not carry the record of what the tool did that day.",
                value: string.Join("; ", errors));
        else if (copied > 0)
            _systemLogger.LogSystemEvent(LogSeverity.Information, "SystemLogMirrored",
                $"{copied} system-log file(s) copied beside the recordings.", value: dayFolder);
    }

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

        // A recording says nothing about the exposure it was taken at, so a baseline built from
        // it later cannot answer the mismatch question. Write the conditions beside it while they
        // are still known. Best-effort: the recording is what matters, and a missing sidecar
        // leaves things exactly as they were before it existed.
        var acquisition = _leakMonitorEngine.CurrentAcquisition;
        foreach (var f in files)
        {
            if (string.IsNullOrEmpty(f)) continue;
            var failure = AcquisitionSidecar.TryWrite(f, acquisition);
            if (failure is not null)
                _systemLogger.LogSystemEvent(LogSeverity.Warning, "AcquisitionSidecarFailed",
                    $"Could not record the acquisition conditions beside {System.IO.Path.GetFileName(f)}: " +
                    $"{failure}. The recording is unaffected, but a Golden Run built from it later " +
                    "will not know the integration time or averaging it was taken at.",
                    value: f);
        }

        var opened = files.FirstOrDefault(f => !string.IsNullOrEmpty(f));
        if (!string.IsNullOrEmpty(opened)) MirrorRunContext(System.IO.Path.GetDirectoryName(opened));
        // Writers opening again is the evidence that whatever stopped the last write is over.
        if (files.Any(f => !string.IsNullOrEmpty(f)))
            _secs.ReportFault(SecsFault.DataWriteFailure, set: false);
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
        _secs.OnLeakLevelChanged(e.NewLevel);
    }

    private void OnGoldenRunCaptured(object? sender, GoldenRun run)
    {
        PersistLeakMonitorSettings($"GoldenRun={run.Name}");
        _systemLogger.LogSystemEvent(LogSeverity.Information, "GoldenRunCaptured",
            $"Leak-monitor Golden Run baseline captured: {run.Name}",
            related: $"Ratios={run.Baselines.Count}",
            // One floor per reference line, named — a bare number told you nothing about which
            // line it applied to, and now there can be more than one.
            value: run.PlasmaFloors.Count == 0
                ? "PlasmaFloor=none"
                : "PlasmaFloor " + string.Join(", ",
                    run.PlasmaFloors.Select(f => $"{f.ReferenceLabel}={f.Floor:G4}")));
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
