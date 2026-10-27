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
/// View-model for the EAP tab: the SECS link to the EAP (scheme 2b) and the main tool's
/// process events it delivers — their status, the switches that decide whether they are used,
/// and the CEID table. Moved off the SECS tab, which now only carries the link to the FDC, so
/// "why is the window unknown?" is answered on one page.
///
/// <para>Same permissions split as the SECS tab: <b>anybody may look</b>, <b>only an Engineer
/// may change anything</b>, so every editable control binds <see cref="CanEdit"/>.</para>
///
/// <para>Two kinds of field, and the tab says which is which. The tool-event switch and the CEID
/// table are <b>staged</b> — adopted when acquisition next starts, because the table names the
/// classes the ratios refer to. Everything else — the window limit, the test-mode switch and the
/// whole EAP link — is <b>hot-applied on save</b>: none of it names a class.</para>
/// </summary>
public sealed class EapViewModel : INotifyPropertyChanged
{
    /// <summary>Lowest and highest window limit Save accepts. A process A step is 38–63 s, so a
    /// limit under 10 s closes every window mid-step; over a day it no longer limits anything.</summary>
    public const double MinWindowSeconds = 10, MaxWindowSecondsLimit = 86400;

    private readonly SecsBridge _bridge;
    private readonly EapSecsLink? _link;
    private readonly Action<ToolEventSettings> _save;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private ToolEventSettings _edited = new();
    private bool _canEdit;
    private bool _dirty;

    // What was last saved, and what the engine adopted at the last acquisition start. The saved
    // table is not in force until then, and the screen has to say so.
    private ToolEventSettings _persisted = new();
    private ToolEventSettings? _inForce;

    public EapViewModel(SecsBridge bridge, ToolEventSettings settings, Action<ToolEventSettings> save,
        EapSecsLink? link = null)
    {
        _bridge = bridge;
        _link = link;
        _save = save;
        SaveCommand = new RelayCommand(Save, () => CanEdit);
        LoadFrom(settings);

        // The engine's tool status reaches the bridge (VIDs 030–032 read it there), so the tab
        // reads the same record rather than a second subscription that could disagree.
        _bridge.ToolContextChanged += (_, _) => _dispatcher.BeginInvoke(RaiseToolStatus);
        if (_link is not null)
            _link.StatusChanged += _ => _dispatcher.BeginInvoke(RaiseLinkStatus);
    }

    public RelayCommand SaveCommand { get; }

    /// <summary>Lines of feedback from the last Save (why it was refused).</summary>
    public string SaveMessage { get; private set; } = "";

    // ---- the EAP link: status (everyone) -------------------------------------

    private EapLinkStatus LinkStatus => _link?.Status ?? EapLinkStatus.Disabled;

    /// <summary>First status line: whether the link is up, and to where.</summary>
    public string EapLinkText
    {
        get
        {
            var s = LinkStatus;
            return s.State switch
            {
                EapLinkState.Disabled => "EAP link: not in use",
                EapLinkState.IdsMissing => $"EAP link: not started — {s.Detail}",
                EapLinkState.Connected => $"EAP link: connected to {s.Endpoint}" +
                                          (s.Peer.Length > 0 ? $" — {s.Peer}" : "") +
                                          (s.ConnectedSince is { } c ? $" (since {c:HH:mm:ss})" : "") +
                                          (s.LinkTestOnly ? " — link test only (EAP ids not set); no events are read" : ""),
                _ => $"EAP link: connecting to {s.Endpoint}" + (s.Detail.Length > 0 ? $" — {s.Detail}" : "…"),
            };
        }
    }

    /// <summary>Second status line: what the last S1F3 read, or why the window is no longer trusted.</summary>
    public string EapDetailText
    {
        get
        {
            var s = LinkStatus;
            if (s.State is EapLinkState.Disabled or EapLinkState.IdsMissing) return "";
            if (s.LinkTestOnly)
                return "Enter the ids the EAP assigned and Save & apply to start reading S1–S7 and receiving its events. " +
                       "Until then the process window is untouched.";
            if (s.Silent)
                return $"Nothing from the EAP for {EapSilenceTimeoutSeconds:0} s — the process window has been set to unknown.";
            if (s.LastReading is not { } r) return "Nothing read yet.";
            string state = r.WindowState switch
            {
                1 => $"in a window (CEID {r.WindowCeid})",
                0 => "outside any window",
                _ => "state unknown",
            };
            string link = r.ToolLink switch { 1 => "up", 0 => "DOWN", _ => "not monitored (no S4)" };
            return $"Last S1F3 {r.ReadAt:HH:mm:ss} · PM3 {state} · tool link {link} · SEQ {r.Sequence}" +
                   (s.LastEvent.Length > 0 ? $" · last event: {s.LastEvent}" : "");
        }
    }

    /// <summary>Third status line, shown only while the EAP provides no S4 or no tool-link CEID:
    /// what the OES then cannot see. Says so rather than let "tool link up" go unmentioned.</summary>
    public string EapToolLinkText
    {
        get
        {
            var s = LinkStatus;
            return s.State is EapLinkState.Disabled or EapLinkState.IdsMissing || s.ToolLinkGap.Length == 0
                ? ""
                : "Tool link not fully monitored — " + s.ToolLinkGap + ".";
        }
    }

    public bool ShowsToolLinkGap => EapToolLinkText.Length > 0;

    public Brush EapLinkBrush => LinkStatus switch
    {
        { State: EapLinkState.Disabled } => Brushes.Gray,
        { State: EapLinkState.IdsMissing } => Brushes.DarkOrange,
        { Silent: true } => Brushes.DarkOrange,
        // Reachable but not yet doing its job: green would read as "events are arriving".
        { State: EapLinkState.Connected, LinkTestOnly: true } => Brushes.DarkOrange,
        { State: EapLinkState.Connected } => Brushes.ForestGreen,
        _ => Brushes.Firebrick,
    };

    public string EapLogFilePath => _link?.LogFilePath ?? "";

    // ---- the EAP link: settings (Engineer+, applied on save) ------------------

    private EapLinkSettings Eap => _edited.Eap ??= new EapLinkSettings();

    public bool EapEnabled { get => Eap.Enabled; set => Set(v => Eap.Enabled = v, Eap.Enabled, value); }
    public string EapHost { get => Eap.RemoteHost; set => Set(v => Eap.RemoteHost = v, Eap.RemoteHost, value); }
    public int EapPort { get => Eap.RemotePort; set => Set(v => Eap.RemotePort = v, Eap.RemotePort, value); }
    public int EapDeviceId { get => Eap.DeviceId; set => Set(v => Eap.DeviceId = v, Eap.DeviceId, value); }

    public double EapSilenceTimeoutSeconds
    {
        get => Eap.SilenceTimeoutSeconds;
        set => Set(v => Eap.SilenceTimeoutSeconds = v, Eap.SilenceTimeoutSeconds, value);
    }

    public uint WindowStateSvid { get => Eap.WindowStateSvid; set => Set(v => Eap.WindowStateSvid = v, Eap.WindowStateSvid, value); }
    public uint WindowCeidSvid { get => Eap.WindowCeidSvid; set => Set(v => Eap.WindowCeidSvid = v, Eap.WindowCeidSvid, value); }
    public uint WindowStartSvid { get => Eap.WindowStartSvid; set => Set(v => Eap.WindowStartSvid = v, Eap.WindowStartSvid, value); }
    public uint ToolLinkSvid { get => Eap.ToolLinkSvid; set => Set(v => Eap.ToolLinkSvid = v, Eap.ToolLinkSvid, value); }
    public uint LastCeidSvid { get => Eap.LastCeidSvid; set => Set(v => Eap.LastCeidSvid = v, Eap.LastCeidSvid, value); }
    public uint LastTimeSvid { get => Eap.LastTimeSvid; set => Set(v => Eap.LastTimeSvid = v, Eap.LastTimeSvid, value); }
    public uint SequenceSvid { get => Eap.SequenceSvid; set => Set(v => Eap.SequenceSvid = v, Eap.SequenceSvid, value); }
    public uint ForwardCeid { get => Eap.ForwardCeid; set => Set(v => Eap.ForwardCeid = v, Eap.ForwardCeid, value); }
    public uint LinkCeid { get => Eap.LinkCeid; set => Set(v => Eap.LinkCeid = v, Eap.LinkCeid, value); }

    // ---- tool events: status (everyone) ---------------------------------------

    /// <summary>The window the leak monitor is judging in, as the engine last published it.</summary>
    public string ToolContextText
    {
        get
        {
            var s = _bridge.ToolStatus;
            return s.State switch
            {
                ToolEventContext.Disabled => "Tool events: not in use",
                ToolEventContext.InWindow => $"Tool events: in window {s.WindowClass}",
                ToolEventContext.OutsideWindow => "Tool events: outside any window",
                _ => "Tool events: window unknown — ratios judged as without tool events " +
                     "(no window edge yet, the last window timed out, the EAP does not know, or not applied to these spectra)",
            };
        }
    }

    /// <summary>The last event forwarded, taken or ignored alike.</summary>
    public string ToolLastEventText => _bridge.ToolStatus.LastTime is { } t
        ? $"Last tool event: CEID {_bridge.ToolStatus.LastCeid} at {t:HH:mm:ss}"
        : "Last tool event: none received yet";

    /// <summary>How many have arrived this run — "is the EAP forwarding anything?" as a number.</summary>
    public string ToolEventCountText => $"Tool events received this run: {_bridge.ToolStatus.ReceivedCount}";

    // ---- tool events: settings (Engineer+) ------------------------------------

    /// <summary>Staged: takes effect when acquisition next starts.</summary>
    public bool ToolEventsEnabled { get => _edited.Enabled; set => Set(v => _edited.Enabled = v, _edited.Enabled, value); }

    /// <summary>Hot-applied on save.</summary>
    public double ToolMaxWindowSeconds
    {
        get => _edited.MaxWindowSeconds;
        set => Set(v => _edited.MaxWindowSeconds = v, _edited.MaxWindowSeconds, value);
    }

    /// <summary>Hot-applied on save.</summary>
    public bool ToolApplyInTestMode
    {
        get => _edited.ApplyInTestMode;
        set => Set(v => _edited.ApplyInTestMode = v, _edited.ApplyInTestMode, value);
    }

    /// <summary>One row of the read-only CEID table, worded for the screen.</summary>
    public sealed record ToolEventMappingRow(uint Ceid, string ClassName, string Edge, string Name, string PlasmaThreshold);

    /// <summary>The CEID table as saved. Read-only here: it is edited in settings.json
    /// (<c>secs.toolEvents.mappings</c>) — it changes rarely, and a grid editor for it would cost
    /// more than it saves.</summary>
    public IReadOnlyList<ToolEventMappingRow> ToolEventMappings => (_edited.Mappings ?? new())
        .Select(m => new ToolEventMappingRow(
            m.Ceid,
            string.IsNullOrWhiteSpace(m.ClassName) ? "(record only)" : m.ClassName.Trim(),
            m.Edge.ToString(),
            m.Name,
            m.PlasmaThreshold > 0
                ? m.PlasmaThreshold.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : "inherit"))
        .ToList();

    /// <summary>
    /// The host reports what the engine adopted — at start-up and at every acquisition start —
    /// so the tab can tell a saved table from the one in force.
    /// </summary>
    public void SetToolEventsInForce(ToolEventSettings? inForce)
    {
        _inForce = inForce?.Clone();
        RaiseToolPending();
    }

    /// <summary>True when the saved switch or table differs from the one the engine is using.</summary>
    public bool ToolTablePending =>
        _inForce is not null && !ToolEventSettings.SameStaged(_persisted, _inForce);

    public string ToolTablePendingText => ToolTablePending
        ? "Saved tool-event switch / CEID table not yet in force — restart acquisition (Stop, then Start) to apply it."
        : "";

    // ---- permissions and saving -------------------------------------------------

    public bool HasUnsavedChanges => _dirty;

    public bool CanEdit
    {
        get => _canEdit;
        private set
        {
            if (_canEdit == value) return;
            _canEdit = value;
            OnPropertyChanged();
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public void SetRole(bool isEngineerOrHigher) => CanEdit = isEngineerOrHigher;

    /// <summary>Replaces the edit buffer with what is on disk.</summary>
    public void LoadFrom(ToolEventSettings? settings)
    {
        _edited = (settings ?? new ToolEventSettings()).Clone();
        _edited.Eap ??= new EapLinkSettings();
        _persisted = _edited.Clone();
        _dirty = false;
        RaiseAll();
    }

    /// <summary>The staged settings, for the host to persist.</summary>
    public ToolEventSettings ToSettings() => _edited.Clone();

    private void Save()
    {
        var problem = Validate();
        SaveMessage = problem.Length > 0 ? "Not saved — " + problem : "";
        OnPropertyChanged(nameof(SaveMessage));
        if (problem.Length > 0) return;
        _save(_edited.Clone());
        _persisted = _edited.Clone();
        _dirty = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        RaiseToolPending();
    }

    /// <summary>Returns "" when the staged settings are usable, otherwise the first problem.</summary>
    private string Validate()
    {
        double window = _edited.MaxWindowSeconds;
        if (double.IsNaN(window) || window < MinWindowSeconds || window > MaxWindowSecondsLimit)
            return $"tool-event max window = {window} s is out of range ({MinWindowSeconds:0}–{MaxWindowSecondsLimit:0}). " +
                   "A process A step is 38–63 s; a limit shorter than a step closes every window mid-step.";
        if (Eap.Enabled && string.IsNullOrWhiteSpace(Eap.RemoteHost))
            return "the EAP link needs the EAP's address.";
        if (Eap.RemotePort is < 1 or > 65535)
            return $"EAP port {Eap.RemotePort} is out of range.";
        if (Eap.DeviceId is < 0 or > 32767)
            return $"EAP device id {Eap.DeviceId} is out of range (0–32767).";
        double silence = Eap.SilenceTimeoutSeconds;
        if (double.IsNaN(silence) || silence < EapLinkSettings.MinSilenceTimeoutSeconds ||
            silence > EapLinkSettings.MaxSilenceTimeoutSeconds)
            return $"EAP silence timeout = {silence} s is out of range " +
                   $"({EapLinkSettings.MinSilenceTimeoutSeconds:0}–{EapLinkSettings.MaxSilenceTimeoutSeconds:0}). " +
                   "The OES reads the EAP every 30 s; a shorter timeout fires on a healthy link.";
        if (Eap.Enabled && Eap.IdProblem() is { } ids)
            return ids + " — enter the ids the EAP assigned (S4 and the tool-link CEID may stay 0 if it provides neither).";
        return "";
    }

    // ---- plumbing ----------------------------------------------------------------

    private void RaiseToolStatus()
    {
        OnPropertyChanged(nameof(ToolContextText));
        OnPropertyChanged(nameof(ToolLastEventText));
        OnPropertyChanged(nameof(ToolEventCountText));
    }

    private void RaiseLinkStatus()
    {
        OnPropertyChanged(nameof(EapLinkText));
        OnPropertyChanged(nameof(EapDetailText));
        OnPropertyChanged(nameof(EapToolLinkText));
        OnPropertyChanged(nameof(ShowsToolLinkGap));
        OnPropertyChanged(nameof(EapLinkBrush));
        OnPropertyChanged(nameof(EapLogFilePath));
    }

    private void RaiseToolPending()
    {
        OnPropertyChanged(nameof(ToolTablePending));
        OnPropertyChanged(nameof(ToolTablePendingText));
    }

    private void RaiseAll()
    {
        RaiseToolStatus();
        RaiseLinkStatus();
        RaiseToolPending();
        foreach (var name in new[]
                 {
                     nameof(HasUnsavedChanges), nameof(ToolEventsEnabled), nameof(ToolMaxWindowSeconds),
                     nameof(ToolApplyInTestMode), nameof(ToolEventMappings), nameof(EapEnabled), nameof(EapHost),
                     nameof(EapPort), nameof(EapDeviceId), nameof(EapSilenceTimeoutSeconds), nameof(WindowStateSvid),
                     nameof(WindowCeidSvid), nameof(WindowStartSvid), nameof(ToolLinkSvid), nameof(LastCeidSvid),
                     nameof(LastTimeSvid), nameof(SequenceSvid), nameof(ForwardCeid), nameof(LinkCeid),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private void Set<T>(Action<T> assign, T current, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        _dirty = true;
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
