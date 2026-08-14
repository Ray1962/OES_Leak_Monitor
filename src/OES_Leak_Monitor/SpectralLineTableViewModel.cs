using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace OES_Leak_Monitor;

/// <summary>
/// One row of the merged emission-line table. Built-in rows are read-only; user rows are
/// editable. The two are shown together deliberately — the question anyone has before adding a
/// line is "is it already in the table?", and that is answered by searching one list, not by
/// choosing which of two lists to look in.
/// </summary>
public sealed class SpectralLineRowViewModel : INotifyPropertyChanged
{
    private readonly bool _builtIn;

    public SpectralLineRowViewModel(SpectralLine line, bool builtIn)
    {
        _builtIn = builtIn;
        _species = line.Species;
        _wavelengthNm = line.WavelengthNm;
    }

    /// <summary>Built-in lines cannot be edited or removed; the fixed table is the reference.</summary>
    public bool IsBuiltIn => _builtIn;
    public bool IsEditable => !_builtIn;

    public string Source => _builtIn ? "built-in" : "user";

    private string _species;
    /// <summary>Full species name as stored — user rows already carry the "u" marker.</summary>
    public string Species
    {
        get => _species;
        set { if (!_builtIn) Set(ref _species, value?.Trim() ?? ""); }
    }

    /// <summary>The species name without its marker, which is what the operator types.</summary>
    public string SpeciesBody
    {
        get => _builtIn || !_species.StartsWith(SpectralLineCatalog.UserPrefix, StringComparison.Ordinal)
            ? _species
            : _species[SpectralLineCatalog.UserPrefix.Length..];
        set
        {
            if (_builtIn) return;
            string body = (value ?? "").Trim();
            Species = body.Length == 0 ? "" : SpectralLineCatalog.UserPrefix + body;
            OnPropertyChanged();
        }
    }

    private double _wavelengthNm;
    public double WavelengthNm
    {
        get => _wavelengthNm;
        set { if (!_builtIn) Set(ref _wavelengthNm, value); }
    }

    public SpectralLine ToLine() => new(_species, _wavelengthNm);

    public UserSpectralLine ToModel() => new() { Species = _species, WavelengthNm = _wavelengthNm };

    /// <summary>Matches the row against the table's search box.</summary>
    public bool Matches(string needle) =>
        needle.Length == 0 ||
        _species.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        _wavelengthNm.ToString("0.###", CultureInfo.InvariantCulture).Contains(needle,
            StringComparison.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(n);
        if (n == nameof(Species)) OnPropertyChanged(nameof(SpeciesBody));
        return true;
    }
}

/// <summary>
/// Backs the emission-line table on the Wavelength Calibration tab: the fixed catalog shown
/// read-only, plus the site's own lines, which can be added, edited, removed, imported and
/// exported. Saving takes effect at once — a line is catalog data, not something the running
/// engine is computing with — unlike the wavelength-correction table on the same tab, which is
/// staged until acquisition restarts. That is why the two have separate Save buttons.
/// </summary>
public sealed class SpectralLineTableViewModel : INotifyPropertyChanged
{
    /// <summary>Hard cap on user-defined lines.</summary>
    public const int MaxUserLines = 100;

    /// <summary>Accepted wavelength range, nm — wide enough for any plausible spectrometer.</summary>
    public const double MinWavelengthNm = 100;
    public const double MaxWavelengthNm = 1200;

    /// <summary>Two lines closer than this are the same line.</summary>
    private const double SameWavelengthTolerance = 0.001;

    private readonly LeakMonitorEngine _engine;
    private readonly Action _persistSettings;
    private readonly SystemLogger? _log;
    private readonly List<SpectralLineRowViewModel> _allRows = new();

    private bool _engineerPlus;

    /// <summary>Raised after the user line table has been saved, so the line pickers elsewhere
    /// can pick the new lines up without waiting for an acquisition restart.</summary>
    public event EventHandler? UserLinesSaved;

    public SpectralLineTableViewModel(LeakMonitorEngine engine, Action persistSettings,
                                      SystemLogger? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _persistSettings = persistSettings ?? throw new ArgumentNullException(nameof(persistSettings));
        _log = log;

        Rows = new ObservableCollection<SpectralLineRowViewModel>();

        AddCommand    = new RelayCommand(AddLine,    () => _engineerPlus && UserLineCount < MaxUserLines);
        RemoveCommand = new RelayCommand(RemoveLine, () => _engineerPlus && _selected is { IsEditable: true });
        SaveCommand   = new RelayCommand(Save,       () => _engineerPlus && _isDirty);
        RevertCommand = new RelayCommand(Load,       () => _isDirty);
        ImportCommand = new RelayCommand(Import,     () => _engineerPlus);
        ExportCommand = new RelayCommand(Export,     () => UserLineCount > 0);

        Load();
    }

    /// <summary>Rows currently shown — the whole table, filtered by search and the user-only switch.</summary>
    public ObservableCollection<SpectralLineRowViewModel> Rows { get; }

    public RelayCommand AddCommand    { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand SaveCommand   { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand ExportCommand { get; }

    private SpectralLineRowViewModel? _selected;
    public SpectralLineRowViewModel? SelectedRow
    {
        get => _selected;
        set { if (Set(ref _selected, value)) RemoveCommand.RaiseCanExecuteChanged(); }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value ?? "")) ApplyFilter(); }
    }

    private bool _userOnly;
    /// <summary>Narrows the table to the site's own lines.</summary>
    public bool UserOnly
    {
        get => _userOnly;
        set { if (Set(ref _userOnly, value)) ApplyFilter(); }
    }

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!Set(ref _isDirty, value)) return;
            SaveCommand.RaiseCanExecuteChanged();
            RevertCommand.RaiseCanExecuteChanged();
        }
    }

    private string _statusMessage = "Built-in lines are read-only. Add your own with the “u” marker.";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public int UserLineCount => _allRows.Count(r => r.IsEditable);

    public string CountText =>
        $"{UserLineCount} of {MaxUserLines} user line(s) · {SpectralLineCatalog.BuiltIn.Count} built-in";

    public void SetRole(bool engineerOrHigher)
    {
        _engineerPlus = engineerOrHigher;
        RaiseCanExec();
    }

    /// <summary>(Re)loads the table from the fixed catalog plus the persisted user lines.</summary>
    public void Load()
    {
        foreach (var row in _allRows) row.PropertyChanged -= OnRowChanged;
        _allRows.Clear();

        foreach (var line in SpectralLineCatalog.BuiltIn)
            _allRows.Add(new SpectralLineRowViewModel(line, builtIn: true));
        foreach (var line in _engine.Settings.UserSpectralLines ?? new List<UserSpectralLine>())
            Track(new SpectralLineRowViewModel(
                new SpectralLine(line.Species, line.WavelengthNm), builtIn: false));

        ApplyFilter();
        IsDirty = false;
        StatusMessage = "Built-in lines are read-only. Add your own with the “u” marker.";
        RaiseCanExec();
        OnPropertyChanged(nameof(CountText));
    }

    private void Track(SpectralLineRowViewModel row)
    {
        row.PropertyChanged += OnRowChanged;
        _allRows.Add(row);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => IsDirty = true;

    private void ApplyFilter()
    {
        string needle = _searchText.Trim();
        Rows.Clear();
        foreach (var row in _allRows)
        {
            if (_userOnly && row.IsBuiltIn) continue;
            if (!row.Matches(needle)) continue;
            Rows.Add(row);
        }
        OnPropertyChanged(nameof(CountText));
    }

    private void AddLine()
    {
        var row = new SpectralLineRowViewModel(
            new SpectralLine(SpectralLineCatalog.UserPrefix + "New", 500.0), builtIn: false);
        Track(row);
        // A new row must be visible whatever the search box says, or Add looks like it did nothing.
        SearchText = "";
        UserOnly = true;
        ApplyFilter();
        SelectedRow = row;
        IsDirty = true;
        RaiseCanExec();
    }

    private void RemoveLine()
    {
        if (_selected is not { IsEditable: true } row) return;

        // A line a ratio is built on cannot be removed. Nothing visible would break today — a
        // RatioDefinition holds its own copy of the region — but the next time someone opens
        // Ratio Setup, the line picker would fail to match the missing species and fall back to
        // the nearest wavelength, silently re-pointing that ratio at a different line. There is
        // no signal at the moment of the deletion and none later either, so it is refused here.
        string? used = DescribeUsage(row.ToLine());
        if (used is not null)
        {
            StatusMessage = $"“{row.Species} {row.WavelengthNm:0.###}” is in use by {used}. " +
                            "Point that away from this line in Ratio Setup (or remove the " +
                            "correction), then delete it here.";
            return;
        }

        row.PropertyChanged -= OnRowChanged;
        _allRows.Remove(row);
        Rows.Remove(row);
        SelectedRow = null;
        IsDirty = true;
        StatusMessage = $"Removed “{row.Species} {row.WavelengthNm:0.###}” — not saved yet.";
        RaiseCanExec();
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>
    /// Names what references a line — a ratio's signal or reference, or a wavelength correction
    /// — or null when nothing does. Ratios are matched the way the engine matches them: species
    /// from the region's label, then the wavelength.
    /// </summary>
    private string? DescribeUsage(SpectralLine line)
    {
        foreach (var def in _engine.Settings.Ratios ?? new List<RatioDefinition>())
        {
            if (Uses(def.Numerator, line))
                return $"ratio “{def.DisplayName}” (signal line)";
            if (def.MonitorMode != MonitorMode.AbsoluteIntensity && Uses(def.Denominator, line))
                return $"ratio “{def.DisplayName}” (reference line)";
        }
        foreach (var c in _engine.Settings.WavelengthCorrections ?? new List<WavelengthCorrection>())
        {
            if (c.Species == line.Species &&
                Math.Abs(c.WavelengthNm - line.WavelengthNm) < SameWavelengthTolerance)
                return "a wavelength correction on this tab";
        }
        return null;
    }

    private static bool Uses(LineRegion? region, SpectralLine line) =>
        region is not null &&
        WavelengthCalibration.SpeciesOf(region.Label) == line.Species &&
        Math.Abs(region.CenterNm - line.WavelengthNm) < 0.5;

    private void Save()
    {
        var users = _allRows.Where(r => r.IsEditable).ToList();

        foreach (var row in users)
        {
            string body = row.SpeciesBody;
            if (string.IsNullOrWhiteSpace(body))
            {
                StatusMessage = "Not saved — a user line has no species name.";
                return;
            }
            if (body.Contains(' '))
            {
                StatusMessage = $"Not saved — species “{body}” contains a space. " +
                                "Use a single token, e.g. N2 or XeCl.";
                return;
            }
            if (row.WavelengthNm < MinWavelengthNm || row.WavelengthNm > MaxWavelengthNm)
            {
                StatusMessage = $"Not saved — {row.Species} {row.WavelengthNm:0.###} nm is outside " +
                                $"{MinWavelengthNm:0}–{MaxWavelengthNm:0} nm.";
                return;
            }
        }

        for (int i = 0; i < users.Count; i++)
        {
            for (int j = i + 1; j < users.Count; j++)
            {
                if (users[i].Species == users[j].Species &&
                    Math.Abs(users[i].WavelengthNm - users[j].WavelengthNm) < SameWavelengthTolerance)
                {
                    StatusMessage = $"Not saved — “{users[i].Species} {users[i].WavelengthNm:0.###}” " +
                                    "appears twice.";
                    return;
                }
            }
        }

        // An edit that moves or renames a line in use is the same silent re-pointing a delete
        // would cause, so it is caught by the same rule — against what is still on disk.
        foreach (var stored in _engine.Settings.UserSpectralLines ?? new List<UserSpectralLine>())
        {
            bool stillThere = users.Any(r =>
                r.Species == stored.Species &&
                Math.Abs(r.WavelengthNm - stored.WavelengthNm) < SameWavelengthTolerance);
            if (stillThere) continue;
            string? used = DescribeUsage(new SpectralLine(stored.Species, stored.WavelengthNm));
            if (used is not null)
            {
                StatusMessage = $"Not saved — “{stored.Species} {stored.WavelengthNm:0.###}” is in " +
                                $"use by {used} and this change would remove or move it. Point that " +
                                "away from the line first, or Revert.";
                return;
            }
        }

        int shadowed = users.Count(r => SpectralLineCatalog.BuiltIn.Any(b =>
            b.Species == r.SpeciesBody &&
            Math.Abs(b.WavelengthNm - r.WavelengthNm) < SameWavelengthTolerance));

        _engine.Settings.UserSpectralLines = users.Select(r => r.ToModel()).ToList();
        SpectralLineCatalog.SetUserLines(_engine.Settings.UserSpectralLines
            .Select(l => new SpectralLine(l.Species, l.WavelengthNm)));
        _persistSettings();
        IsDirty = false;

        StatusMessage = $"Saved {users.Count} user line(s) — available in Ratio Setup now, no " +
                        "acquisition restart needed." +
                        (shadowed > 0
                            ? $" Note: {shadowed} of them duplicate a built-in line's wavelength."
                            : "");
        _log?.LogSystemEvent(LogSeverity.Information, "UserSpectralLinesSaved",
            "User-defined emission lines saved to the catalog overlay",
            value: $"Lines={users.Count}");
        UserLinesSaved?.Invoke(this, EventArgs.Empty);
        RaiseCanExec();
        OnPropertyChanged(nameof(CountText));
    }

    // --- import / export ------------------------------------------------------

    /// <summary>
    /// Merges a CSV of <c>Species,WavelengthNm</c> into the user lines. Merge, never replace:
    /// an import can then never remove a line a ratio is built on, so it needs no second
    /// in-use check. Lines already present are counted and skipped.
    /// </summary>
    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import emission lines",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        int added = 0, skipped = 0, rejected = 0;
        try
        {
            foreach (var raw in File.ReadLines(dlg.FileName))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(',');
                if (parts.Length < 2) { rejected++; continue; }

                string body = parts[0].Trim().TrimStart('﻿');
                if (body.StartsWith(SpectralLineCatalog.UserPrefix, StringComparison.Ordinal) &&
                    body.Length > SpectralLineCatalog.UserPrefix.Length)
                    body = body[SpectralLineCatalog.UserPrefix.Length..];
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float,
                                     CultureInfo.InvariantCulture, out double wl))
                {
                    // The header row lands here, which is why it is skipped silently.
                    continue;
                }
                if (body.Length == 0 || body.Contains(' ') ||
                    wl < MinWavelengthNm || wl > MaxWavelengthNm)
                {
                    rejected++;
                    continue;
                }

                string species = SpectralLineCatalog.UserPrefix + body;
                bool exists = _allRows.Any(r => r.IsEditable && r.Species == species &&
                    Math.Abs(r.WavelengthNm - wl) < SameWavelengthTolerance);
                if (exists) { skipped++; continue; }
                if (UserLineCount >= MaxUserLines)
                {
                    StatusMessage = $"Stopped at the {MaxUserLines}-line cap — imported {added}, " +
                                    $"skipped {skipped} already present.";
                    ApplyFilter();
                    IsDirty = true;
                    RaiseCanExec();
                    return;
                }

                Track(new SpectralLineRowViewModel(new SpectralLine(species, wl), builtIn: false));
                added++;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
            _log?.LogError("UserSpectralLines_Import_Failed", ex, dlg.FileName);
            return;
        }

        UserOnly = true;
        SearchText = "";
        ApplyFilter();
        if (added > 0) IsDirty = true;
        StatusMessage = $"Imported {added} line(s), skipped {skipped} already present" +
                        (rejected > 0 ? $", rejected {rejected} unreadable" : "") +
                        ". Press Save to keep them.";
        RaiseCanExec();
        OnPropertyChanged(nameof(CountText));
    }

    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export user emission lines",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = "user-emission-lines.csv",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Species,WavelengthNm");
            foreach (var row in _allRows.Where(r => r.IsEditable)
                                        .OrderBy(r => r.Species, StringComparer.Ordinal)
                                        .ThenBy(r => r.WavelengthNm))
                sb.AppendLine(FormattableString.Invariant($"{row.Species},{row.WavelengthNm:0.###}"));
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            StatusMessage = $"Exported {UserLineCount} user line(s) to {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
            _log?.LogError("UserSpectralLines_Export_Failed", ex, dlg.FileName);
        }
    }

    private void RaiseCanExec()
    {
        AddCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();
        ImportCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
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
