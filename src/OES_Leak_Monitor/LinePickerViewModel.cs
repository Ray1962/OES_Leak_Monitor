using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace OES_Leak_Monitor;

/// <summary>
/// Fills the logger's two wavelength fields from the emission-line catalog instead of from
/// memory. It is an <em>input aid</em>, deliberately: the values still live in the LoggerPanel's
/// own boxes (that panel comes from the framework package and cannot be replaced from here), and
/// typing a wavelength by hand stays exactly as valid. Because the picker owns no state, there
/// is no second copy of the setting to drift out of step with the first.
/// </summary>
public sealed class LinePickerViewModel : INotifyPropertyChanged
{
    /// <summary>Two monitored entries closer than this are the same line for a recorder.</summary>
    private const double SameWavelengthNm = 0.05;

    /// <summary>How many monitored wavelengths the Monitor tab's trend chart draws.</summary>
    private const int TrendLineLimit = WavelengthTrendViewModel.MaxMonitoredWavelengths;

    private readonly LoggerViewModel _logger;
    private readonly LeakMonitorEngine _engine;
    private readonly ObservableCollection<SpectralLineOption> _options;

    public LinePickerViewModel(LoggerViewModel logger, LeakMonitorEngine engine)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        _options = new ObservableCollection<SpectralLineOption>(
            SpectralLineCatalog.All.Select(l => new SpectralLineOption(l)));

        // Species-grouped, same shape as the Ratio Setup line pickers, so a catalog that is
        // already familiar there reads the same way here.
        var cvs = new CollectionViewSource { Source = _options };
        cvs.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SpectralLineOption.Species)));
        cvs.SortDescriptions.Add(new SortDescription(nameof(SpectralLineOption.Species),
                                                     ListSortDirection.Ascending));
        cvs.SortDescriptions.Add(new SortDescription(nameof(SpectralLineOption.WavelengthNm),
                                                     ListSortDirection.Ascending));
        Lines = cvs.View;

        SetTriggerCommand   = new RelayCommand(SetTrigger,   () => _selected is not null);
        AddMonitoredCommand = new RelayCommand(AddMonitored, () => _selected is not null);

        RefreshCatalog();
    }

    /// <summary>Species-grouped catalog, built-in and user-defined lines together.</summary>
    public ICollectionView Lines { get; }

    public RelayCommand SetTriggerCommand   { get; }
    public RelayCommand AddMonitoredCommand { get; }

    private SpectralLineOption? _selected;
    public SpectralLineOption? SelectedLine
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            SetTriggerCommand.RaiseCanExecuteChanged();
            AddMonitoredCommand.RaiseCanExecuteChanged();
        }
    }

    private string _hintText =
        "Pick a line, then send it to the trigger or the monitored list. Values still go through Apply / Save.";
    /// <summary>What the last action did, and anything about it worth knowing.</summary>
    public string HintText { get => _hintText; private set => Set(ref _hintText, value); }

    /// <summary>Re-reads the catalog and the correction overlay — new user-defined lines appear
    /// here as soon as they are saved, the same as in the Ratio Setup pickers.</summary>
    public void RefreshCatalog()
    {
        var wanted = SpectralLineCatalog.All;
        var have = new HashSet<(string, double)>(
            _options.Select(o => (o.Species, Math.Round(o.WavelengthNm, 3))));

        foreach (var line in wanted)
        {
            if (have.Add((line.Species, Math.Round(line.WavelengthNm, 3))))
                _options.Add(new SpectralLineOption(line));
        }

        var keep = new HashSet<(string, double)>(
            wanted.Select(l => (l.Species, Math.Round(l.WavelengthNm, 3))));
        for (int i = _options.Count - 1; i >= 0; i--)
        {
            var o = _options[i];
            if (!keep.Contains((o.Species, Math.Round(o.WavelengthNm, 3))))
                _options.RemoveAt(i);
        }

        var lookup = WavelengthCalibration.Build(_engine.Settings.WavelengthCorrections);
        foreach (var o in _options)
            o.OffsetNm = lookup.TryGetValue((o.Species, Math.Round(o.WavelengthNm, 3)), out double off)
                ? off : 0.0;

        Lines.Refresh();
    }

    private void SetTrigger()
    {
        if (_selected is not { } line) return;
        _logger.TriggerWavelength = (float)line.WavelengthNm;
        HintText = $"Trigger wavelength set to {Describe(line)}." + CorrectionNote(line) + TriggerModeNote();
    }

    private void AddMonitored()
    {
        if (_selected is not { } line) return;

        var existing = ParseMonitored(_logger.MonitoredWavelengthsText);
        if (existing.Any(w => Math.Abs(w - line.WavelengthNm) < SameWavelengthNm))
        {
            HintText = $"{Describe(line)} is already in the monitored list.";
            return;
        }

        existing.Add(line.WavelengthNm);
        _logger.MonitoredWavelengthsText = string.Join(", ",
            existing.Select(w => w.ToString("0.###", CultureInfo.InvariantCulture)));

        // The two limits are different things: the CSV takes every monitored wavelength as a
        // column, the trend chart draws the first few. Say so rather than capping the list,
        // which would make wanting eight columns cost you the chart.
        string overflow = existing.Count > TrendLineLimit
            ? $" The trend chart draws the first {TrendLineLimit}; the rest are still logged to the CSV."
            : "";
        HintText = $"Added {Describe(line)} to the monitored list ({existing.Count} total)."
                 + CorrectionNote(line) + overflow;
    }

    private static string Describe(SpectralLineOption line) =>
        $"{line.Species} {line.WavelengthNm:0.###} nm";

    /// <summary>
    /// The catalog wavelength is what gets written, never the corrected one. A recorder that
    /// held "777.4" would match neither the catalog nor the correction table once someone
    /// retuned the offset — a number with no derivation. The recorder's own wavelength
    /// tolerance is wider than these corrections anyway; where it isn't, this says so.
    /// </summary>
    private static string CorrectionNote(SpectralLineOption line) =>
        Math.Abs(line.OffsetNm) < 1e-9
            ? ""
            : $" This line carries a {line.OffsetNm:+0.###;-0.###} nm calibration correction; the " +
              "recorder does not apply it, so the catalog wavelength was written.";

    /// <summary>
    /// A trigger wavelength is inert in the whole-frame trigger modes. The mode is not changed
    /// for the operator — percentile mode is chosen deliberately, for a recipe that admits the
    /// watched gas only in some steps — but neither is it left unsaid.
    /// </summary>
    private string TriggerModeNote() =>
        _logger.TriggerMode == TriggerMode.Wavelength
            ? ""
            : $" Note: Trigger mode is {_logger.TriggerMode}, so triggering does not use this " +
              "wavelength — it stays the selected line on the trend chart.";

    private static List<double> ParseMonitored(string? text)
    {
        var list = new List<double>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var part in text.Split(',', ';'))
        {
            if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                                out double w))
                list.Add(w);
        }
        return list;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(n);
        return true;
    }
}
