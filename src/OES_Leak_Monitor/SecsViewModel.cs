using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace OES_Leak_Monitor;

/// <summary>
/// View-model for the SECS tab: what the interface is doing, and the settings that decide it.
///
/// <para>The split of permissions is deliberate and differs from the Configuration / Replay
/// tabs, which are gated whole. Here <b>anybody may look</b> — an Operator asked "is the tool
/// talking to the MES?" needs an answer without a sign-in — while <b>only an Engineer may
/// change anything</b>. So the tab has no gate; every editable control binds
/// <see cref="CanEdit"/>.</para>
///
/// <para>Edits are staged: they are typed into this view-model and take effect on Save, which
/// persists them and rebuilds the interface. Nothing is applied as it is typed — half a port
/// number is not an address.</para>
/// </summary>
public sealed class SecsViewModel : INotifyPropertyChanged
{
    /// <summary>Lines kept for the tab. Enough to cover a connection attempt and a few
    /// exchanges; the file is what you read for anything longer.</summary>
    public const int MaxTrafficLines = 500;

    private readonly SecsBridge _bridge;
    private readonly Action<SecsSettings> _save;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    // Pending edits. Applied to the running interface only by Save.
    private SecsSettings _edited = new();
    private bool _canEdit;
    private bool _dirty;

    public SecsViewModel(SecsBridge bridge, SecsSettings settings, Action<SecsSettings> save)
    {
        _bridge = bridge;
        _save = save;

        SaveCommand = new RelayCommand(Save, () => CanEdit);
        RestartCommand = new RelayCommand(Restart, () => CanEdit);
        ClearTrafficCommand = new RelayCommand(() => Traffic.Clear());

        LoadFrom(settings);

        _bridge.StateChanged += (_, _) => _dispatcher.BeginInvoke(RaiseStatus);
        _bridge.Traffic += (_, line) => _dispatcher.BeginInvoke(() => AppendTraffic(line));
    }

    // ---- commands ----------------------------------------------------------

    public RelayCommand SaveCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand ClearTrafficCommand { get; }

    /// <summary>The most recent traffic lines, newest last.</summary>
    public ObservableCollection<string> Traffic { get; } = new();

    // ---- status (read-only, visible to everyone) ---------------------------

    public SecsRunState State => _bridge.State;

    public string StateText => _bridge.State switch
    {
        SecsRunState.Disabled => "DISABLED",
        SecsRunState.Listening => "LISTENING",
        SecsRunState.Selected => "SELECTED",
        SecsRunState.Communicating => "COMMUNICATING",
        _ => "FAILED",
    };

    public Brush StateBrush => _bridge.State switch
    {
        SecsRunState.Communicating => Brushes.SeaGreen,
        SecsRunState.Selected => Brushes.DarkOrange,
        SecsRunState.Listening => Brushes.SteelBlue,
        SecsRunState.Failed => Brushes.Firebrick,
        _ => Brushes.Gray,
    };

    public string StatusDetail => _bridge.State == SecsRunState.Failed
        ? _bridge.LastError
        : _bridge.StatusText;

    public string ControlStateText => _bridge.ControlStateText;

    public string ProfileTemplatePath => _bridge.ProfileTemplatePath;
    public string EffectiveProfilePath => _bridge.EffectiveProfilePath;
    public string LogFilePath => _bridge.LogFilePath;

    /// <summary>
    /// What is actually being reported right now, in one sentence. This exists because the
    /// three switches interact: a tool that is "COMMUNICATING" with reporting off looks
    /// healthy and is telling the host nothing.
    /// </summary>
    public string ReportingSummary
    {
        get
        {
            if (!_edited.Enabled)
            {
                return "The SECS interface is switched off.";
            }
            var parts = new List<string>();
            parts.Add(_edited.ReportAlarms ? "alarms on" : "alarms OFF");
            parts.Add(_edited.ReportEvents ? "events on" : "events OFF");
            parts.Add(_edited.ReportInTestMode
                ? "test/replay data IS reported"
                : "test/replay data is not reported");
            return string.Join(", ", parts) + ". Status queries always answer.";
        }
    }

    /// <summary>Chamber name for the current code, so a mistyped code is visible as such.</summary>
    public string ChamberName
    {
        get
        {
            var name = SecsChamberCoding.ChamberName(_edited.ChamberCode);
            return name.Length > 0
                ? $"{_edited.ChamberCode:00} — {name}"
                : $"{_edited.ChamberCode:00} — not a chamber in the specification";
        }
    }

    /// <summary>Worked example of the ids this chamber produces, so they can be checked against
    /// the host's configuration without reading the profile file.</summary>
    public string IdExample =>
        $"Leak rate SVID {SecsChamberCoding.Svid(_edited.ChamberCode, 1)}, " +
        $"leak alarm ALID {SecsChamberCoding.EventId(_edited.ChamberCode, 2)}, " +
        $"acquisition-started CEID {SecsChamberCoding.EventId(_edited.ChamberCode, 508)}";

    public bool HasUnsavedChanges => _dirty;

    // ---- settings (editable by Engineer+) ----------------------------------

    /// <summary>Whether the settings controls are live. False for Guest / Operator.</summary>
    public bool CanEdit
    {
        get => _canEdit;
        private set
        {
            if (_canEdit == value) return;
            _canEdit = value;
            OnPropertyChanged();
            SaveCommand.RaiseCanExecuteChanged();
            RestartCommand.RaiseCanExecuteChanged();
        }
    }

    public void SetRole(bool isEngineerOrHigher) => CanEdit = isEngineerOrHigher;

    public bool Enabled
    {
        get => _edited.Enabled;
        set => Set(v => _edited.Enabled = v, _edited.Enabled, value, alsoRaise: nameof(ReportingSummary));
    }

    public bool ReportAlarms
    {
        get => _edited.ReportAlarms;
        set => Set(v => _edited.ReportAlarms = v, _edited.ReportAlarms, value, alsoRaise: nameof(ReportingSummary));
    }

    public bool ReportEvents
    {
        get => _edited.ReportEvents;
        set => Set(v => _edited.ReportEvents = v, _edited.ReportEvents, value, alsoRaise: nameof(ReportingSummary));
    }

    public bool ReportInTestMode
    {
        get => _edited.ReportInTestMode;
        set => Set(v => _edited.ReportInTestMode = v, _edited.ReportInTestMode, value, alsoRaise: nameof(ReportingSummary));
    }

    public int ChamberCode
    {
        get => _edited.ChamberCode;
        set => Set(v => _edited.ChamberCode = v, _edited.ChamberCode, value,
            alsoRaise: nameof(ChamberName), also2: nameof(IdExample));
    }

    /// <summary>Chamber codes for the picker, each shown with its name.</summary>
    public IReadOnlyList<ChamberOption> Chambers { get; } = SecsChamberCoding.ValidChamberCodes
        .Select(c => new ChamberOption(c, $"{c:00} — {SecsChamberCoding.ChamberName(c)}"))
        .ToList();

    public sealed record ChamberOption(int Code, string Display);

    public bool IsActive
    {
        get => _edited.IsActive;
        set => Set(v => _edited.IsActive = v, _edited.IsActive, value);
    }

    public string IpAddress
    {
        get => _edited.IpAddress;
        set => Set(v => _edited.IpAddress = v ?? "", _edited.IpAddress, value ?? "");
    }

    public int Port
    {
        get => _edited.Port;
        set => Set(v => _edited.Port = v, _edited.Port, value);
    }

    public int DeviceId
    {
        get => _edited.DeviceId;
        set => Set(v => _edited.DeviceId = v, _edited.DeviceId, value);
    }

    public string ModelName
    {
        get => _edited.ModelName;
        set => Set(v => _edited.ModelName = v ?? "", _edited.ModelName, value ?? "");
    }

    public string SoftwareRevision
    {
        get => _edited.SoftwareRevision;
        set => Set(v => _edited.SoftwareRevision = v ?? "", _edited.SoftwareRevision, value ?? "");
    }

    public int T3 { get => _edited.T3; set => Set(v => _edited.T3 = v, _edited.T3, value); }
    public int T5 { get => _edited.T5; set => Set(v => _edited.T5 = v, _edited.T5, value); }
    public int T6 { get => _edited.T6; set => Set(v => _edited.T6 = v, _edited.T6, value); }
    public int T7 { get => _edited.T7; set => Set(v => _edited.T7 = v, _edited.T7, value); }
    public int T8 { get => _edited.T8; set => Set(v => _edited.T8 = v, _edited.T8, value); }

    public int LogRetentionDays
    {
        get => _edited.LogRetentionDays;
        set => Set(v => _edited.LogRetentionDays = v, _edited.LogRetentionDays, value);
    }

    // ---- operations --------------------------------------------------------

    /// <summary>Replaces the edit buffer with what is on disk. Called at construction and after
    /// a Load Defaults elsewhere reloads the file.</summary>
    public void LoadFrom(SecsSettings settings)
    {
        _edited = Clone(settings ?? new SecsSettings());
        _dirty = false;
        RaiseAll();
    }

    /// <summary>The staged settings, for the host to persist.</summary>
    public SecsSettings ToSettings() => Clone(_edited);

    private void Save()
    {
        var validation = Validate();
        if (validation.Length > 0)
        {
            AppendTraffic("[CFG] not saved — " + validation);
            OnPropertyChanged(nameof(StatusDetail));
            return;
        }
        _save(Clone(_edited));
        _dirty = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void Restart()
    {
        // Restart runs what is *saved*, not what is typed — otherwise the interface and
        // settings.json would disagree and nobody could tell which was live.
        _save(Clone(_edited));
        _dirty = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>Returns "" when the staged settings are usable, otherwise the first problem.</summary>
    private string Validate()
    {
        if (_edited.Enabled && !SecsChamberCoding.IsValidChamber(_edited.ChamberCode))
        {
            return $"chamber code {_edited.ChamberCode:00} is not one the specification defines. " +
                   "Every SVID, ALID and CEID is derived from it.";
        }
        if (_edited.Port is < 1 or > 65535)
        {
            return $"port {_edited.Port} is out of range.";
        }
        if (_edited.DeviceId is < 0 or > 65535)
        {
            return $"device id {_edited.DeviceId} is out of range.";
        }
        if (string.IsNullOrWhiteSpace(_edited.IpAddress))
        {
            return "an address is required (0.0.0.0 listens on every adapter).";
        }
        foreach (var (name, value) in new[]
                 {
                     ("T3", _edited.T3), ("T5", _edited.T5), ("T6", _edited.T6),
                     ("T7", _edited.T7), ("T8", _edited.T8),
                 })
        {
            if (value is < 1 or > 3600)
            {
                return $"{name} = {value} s is out of range (1–3600).";
            }
        }
        return "";
    }

    // ---- plumbing ----------------------------------------------------------

    private void AppendTraffic(string line)
    {
        Traffic.Add(line);
        while (Traffic.Count > MaxTrafficLines)
        {
            Traffic.RemoveAt(0);
        }
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(ControlStateText));
        OnPropertyChanged(nameof(ProfileTemplatePath));
        OnPropertyChanged(nameof(EffectiveProfilePath));
        OnPropertyChanged(nameof(LogFilePath));
    }

    private void RaiseAll()
    {
        RaiseStatus();
        foreach (var name in new[]
                 {
                     nameof(Enabled), nameof(ReportAlarms), nameof(ReportEvents), nameof(ReportInTestMode),
                     nameof(ChamberCode), nameof(ChamberName), nameof(IdExample), nameof(IsActive),
                     nameof(IpAddress), nameof(Port), nameof(DeviceId), nameof(ModelName),
                     nameof(SoftwareRevision), nameof(T3), nameof(T5), nameof(T6), nameof(T7), nameof(T8),
                     nameof(LogRetentionDays), nameof(ReportingSummary), nameof(HasUnsavedChanges),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private void Set<T>(Action<T> assign, T current, T value,
        [CallerMemberName] string? property = null, string? alsoRaise = null, string? also2 = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return;
        }
        assign(value);
        _dirty = true;
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(HasUnsavedChanges));
        if (alsoRaise is not null) OnPropertyChanged(alsoRaise);
        if (also2 is not null) OnPropertyChanged(also2);
    }

    private static SecsSettings Clone(SecsSettings s) => new()
    {
        Enabled = s.Enabled,
        ReportAlarms = s.ReportAlarms,
        ReportEvents = s.ReportEvents,
        ReportInTestMode = s.ReportInTestMode,
        ChamberCode = s.ChamberCode,
        IsActive = s.IsActive,
        IpAddress = s.IpAddress,
        Port = s.Port,
        DeviceId = s.DeviceId,
        ModelName = s.ModelName,
        SoftwareRevision = s.SoftwareRevision,
        T3 = s.T3, T5 = s.T5, T6 = s.T6, T7 = s.T7, T8 = s.T8,
        ProfileFileName = s.ProfileFileName,
        LogRetentionDays = s.LogRetentionDays,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
