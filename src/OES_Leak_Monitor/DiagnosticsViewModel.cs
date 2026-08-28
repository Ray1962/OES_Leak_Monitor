using System;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace OES_Leak_Monitor;

/// <summary>
/// The one-click diagnostic bundle, on the Logs tab.
///
/// <para><b>Not gated, on purpose.</b> The person who can press this is the one on the phone —
/// an Operator — and a diagnostic an Operator cannot produce is a diagnostic that does not exist.
/// It is the same split the SECS tab and the Monitor tab's recorder strip already keep: seeing
/// the state and handing over the evidence is an Operator's business, changing settings is not.
/// This button changes nothing; it only reads and copies.</para>
///
/// <para>Owns no state of its own beyond "am I running" — everything in the bundle is gathered by
/// the callback <see cref="MainViewModel"/> supplies, which reads the same live objects the rest
/// of the app does.</para>
/// </summary>
public sealed class DiagnosticsViewModel : INotifyPropertyChanged
{
    /// <summary>Sub-folder of the app's AppData root. Named so it is obvious what to delete.</summary>
    public const string FolderName = "Diagnostics";

    private readonly Dispatcher _dispatcher;
    private readonly Func<DiagnosticInputs> _gather;
    private readonly SystemLogger _log;
    private readonly string _folder;
    private readonly Action<string, string>? _reveal;

    private bool _busy;
    private string _status = "";
    private string _lastBundlePath = "";

    public DiagnosticsViewModel(string appDataRoot, Func<DiagnosticInputs> gather,
                                SystemLogger log, Dispatcher dispatcher,
                                Action<string, string>? reveal = null)
    {
        _folder = Path.Combine(appDataRoot, FolderName);
        _gather = gather ?? throw new ArgumentNullException(nameof(gather));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _reveal = reveal ?? RevealInExplorer;
        CreateCommand = new RelayCommand(() => _ = RunAsync(), () => !_busy);
    }

    public RelayCommand CreateCommand { get; }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (_busy == value) return;
            _busy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ButtonText));
            CreateCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Progress is the answer to the only question being asked while it runs.</summary>
    public string ButtonText => IsBusy ? "Collecting…" : "Create diagnostic bundle";

    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    public string LastBundlePath
    {
        get => _lastBundlePath;
        private set { _lastBundlePath = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Collects on a worker thread. Never on the acquisition thread and never on the UI thread:
    /// a 300 MB recording on a fab machine's spinning disk is tens of seconds, and the tool is
    /// usually measuring while somebody presses this.
    ///
    /// <para>There is no cancel. A half-written bundle is rubbish, and the question being asked
    /// during the wait is "is it done yet", which progress answers.</para>
    /// </summary>
    public async Task RunAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Collecting…";

        try
        {
            // Gathered on the UI thread: it reads view-models the UI owns. Only the copying,
            // which is all of the cost, goes to the worker.
            var inputs = _gather();
            var now = DateTime.Now;
            var name = DiagnosticBundle.FileNameFor(inputs.Environment.MachineName,
                                                    inputs.Environment.ChamberCode, now);
            var target = Path.Combine(_folder, name);

            var result = await Task.Run(() =>
            {
                var r = DiagnosticBundle.Write(target, inputs, now);
                DiagnosticBundle.Prune(_folder);
                return r;
            }).ConfigureAwait(true);

            LastBundlePath = result.Path;
            var missing = result.Manifest.Missing.Count();
            Status = $"{Path.GetFileName(result.Path)} — {Mb(result.Bytes)}"
                   + (missing > 0 ? $", {missing} item(s) not included (see README.txt)" : "");

            // Into the audit log as well as onto the screen. The Explorer window gets closed, and
            // the next sentence on the phone is always "where is it".
            _log.LogSystemEvent(LogSeverity.Information, "DiagnosticBundleCreated",
                $"Diagnostic bundle written ({Mb(result.Bytes)}, {missing} item(s) omitted)",
                related: $"Items={result.Manifest.Items.Count},Omitted={missing}",
                value: result.Path);

            _reveal?.Invoke(result.Path, _folder);
        }
        catch (Exception ex)
        {
            Status = $"Failed: {ex.Message}";
            _log.LogSystemEvent(LogSeverity.Error, "DiagnosticBundleFailed",
                $"Diagnostic bundle could not be written: {ex.Message}",
                value: _folder);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Mb(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.#} MB" : $"{bytes / 1024.0:0.#} KB";

    /// <summary>Selects the file so it can be dragged straight into an email.</summary>
    private static void RevealInExplorer(string file, string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"/select,\"{file}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Not worth a dialog: the path is on screen and in the log either way.
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
