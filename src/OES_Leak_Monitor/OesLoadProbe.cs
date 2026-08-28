using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OES_Leak_Monitor;

/// <summary>
/// What the app swallows: whether the OES native DLLs can actually be loaded, and the Win32 error
/// when they cannot.
///
/// <para><c>OesDevice.CheckHardwareDllAvailability</c> catches a failed load into
/// <c>SetupTestMode()</c>, which the SDK reports as a <i>successful</i> connect. On 2026-08-17 a
/// fab PC therefore streamed synthetic spectra into the real data folder for a whole session while
/// reporting a healthy connect, and the one fact that would have ended it in a minute —
/// <c>LoadLibrary</c> failing with Win32 126 because the machine had never had the Visual C++
/// redistributable — reached nothing: not the log, not the screen, not the CSV. Recovering it
/// needed <c>tools/check-oes-connect.ps1</c>, a script written for that afternoon.</para>
///
/// <para>This is that script's sections 1–4 run inside the app. It is deliberately <b>not</b> the
/// script's section 5: enumerating USB devices means re-entering the native SDK while it may be
/// acquiring, and a diagnostic that can disturb the measurement is one nobody dares press. The
/// device's own live state answers that half instead — see <see cref="DiagnosticEnvironment"/>.</para>
///
/// <para>The script stays in the repo and is not superseded by this. When the app will not start,
/// it is the only one of the two that can run.</para>
/// </summary>
public static class OesLoadProbe
{
    /// <summary>Named by the script, and by the postmortem's checklist, so it keeps that name.</summary>
    public const string FileName = "oes-diagnostic.txt";

    /// <summary>Native DLLs <c>FlattenOesNativeDlls</c> puts next to the .exe.</summary>
    private static readonly string[] OesNative =
        { "UserApplication.dll", "SiUSBXp.dll", "libsodium.dll" };

    /// <summary>The C++ runtime <c>CopyVcRuntime</c> ships app-local. libsodium imports the first.</summary>
    private static readonly string[] VcRuntime =
        { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" };

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    private const uint LoadWithAlteredSearchPath = 0x8;

    /// <summary>
    /// Runs the probe against <paramref name="appFolder"/> (the app base directory in production;
    /// a fixture folder under test) and returns the report text.
    ///
    /// <para>Never throws. A probe that fails is itself the finding, and the bundle it belongs to
    /// has to be produced anyway — the caller is holding a machine that is already misbehaving.</para>
    /// </summary>
    public static string Run(string appFolder)
    {
        var sb = new StringBuilder();
        try
        {
            Report(sb, appFolder);
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine("!! The probe itself failed. That is a finding, not a reason to stop:");
            sb.AppendLine($"   {ex.GetType().Name}: {ex.Message}");
        }
        return sb.ToString();
    }

    private static void Report(StringBuilder sb, string appFolder)
    {
        sb.AppendLine("OES load probe  -  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(new string('-', 66));
        sb.AppendLine($"Folder        : {appFolder}");
        sb.AppendLine($"Process       : {(Environment.Is64BitProcess ? "x64" : "x86 (WRONG - the app is x64)")}");
        sb.AppendLine($"User          : {Environment.UserName}");
        sb.AppendLine($"OS            : {RuntimeInformation.OSDescription}");
        sb.AppendLine();

        sb.AppendLine("1. OES native DLLs that must sit next to the .exe");
        foreach (var name in OesNative) ReportFile(sb, appFolder, name);
        sb.AppendLine();

        sb.AppendLine("2. Visual C++ runtime (libsodium.dll imports VCRUNTIME140.dll)");
        sb.AppendLine("   Missing here means Win32 126 before a single device is enumerated, and");
        sb.AppendLine("   the app reports a healthy connect anyway. vcruntime140_cor3.dll is WPF's");
        sb.AppendLine("   renamed private copy and does NOT satisfy that import.");
        foreach (var name in VcRuntime) ReportFile(sb, appFolder, name);
        sb.AppendLine();

        sb.AppendLine("3. UserApplication.dll bitness");
        sb.AppendLine("   " + DescribeBitness(Path.Combine(appFolder, "UserApplication.dll")));
        sb.AppendLine();

        sb.AppendLine("4. Native load test (what CheckHardwareDllAvailability does, and swallows)");
        ReportLoad(sb, Path.Combine(appFolder, "UserApplication.dll"));
    }

    private static void ReportFile(StringBuilder sb, string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            sb.AppendLine($"   MISSING {name}");
            return;
        }
        // A file copied out of a zip carries a Zone.Identifier stream and Windows may refuse to
        // load it. It looks present in every listing, which is why this is checked and not assumed.
        var blocked = File.Exists(path + ":Zone.Identifier") ? "   [BLOCKED - right-click > Unblock]" : "";
        sb.AppendLine($"   OK      {name,-24} {info.Length,12:N0} bytes{blocked}");
    }

    /// <summary>Reads the PE header rather than trusting the folder it was found in.</summary>
    private static string DescribeBitness(string path)
    {
        if (!File.Exists(path)) return "MISSING - cannot read the PE header";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            fs.Seek(0x3C, SeekOrigin.Begin);
            int peOffset = br.ReadInt32();
            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550) return "not a PE image";
            var machine = br.ReadUInt16();
            return machine switch
            {
                0x8664 => "x64  (correct for this app)",
                0x014c => "x86  (WRONG - this app is x64-only; the load will fail with Win32 193)",
                0xAA64 => "ARM64 (WRONG for this app)",
                _ => $"unknown machine type 0x{machine:X4}",
            };
        }
        catch (Exception ex)
        {
            return $"could not be read: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// The one line the whole file exists for: does it load, and if not, what did Windows say.
    /// LOAD_WITH_ALTERED_SEARCH_PATH so the DLL's own folder is searched for its dependencies,
    /// which is how the app's DllResolver reaches it.
    /// </summary>
    private static void ReportLoad(StringBuilder sb, string path)
    {
        if (!File.Exists(path))
        {
            sb.AppendLine("   MISSING UserApplication.dll - nothing to load. Hardware connect");
            sb.AppendLine("   cannot succeed, and the app will report test mode as a success.");
            return;
        }

        var handle = LoadLibraryExW(path, IntPtr.Zero, LoadWithAlteredSearchPath);
        if (handle != IntPtr.Zero)
        {
            FreeLibrary(handle);
            sb.AppendLine("   LOADED  UserApplication.dll and every dependency resolved.");
            sb.AppendLine("   A test-mode fallback from here is 'no device found', not 'DLL failed'.");
            return;
        }

        int err = Marshal.GetLastWin32Error();
        sb.AppendLine($"   FAILED  Win32 {err}: {new Win32Exception(err).Message.TrimEnd('.')}");
        sb.AppendLine("   " + err switch
        {
            126 => "ERROR_MOD_NOT_FOUND - a dependency is missing, not this DLL. Almost always "
                 + "the Visual C++ 2015-2022 x64 runtime; see section 2.",
            193 => "ERROR_BAD_EXE_FORMAT - bitness mismatch; see section 3.",
            5   => "ERROR_ACCESS_DENIED - blocked file or folder permissions; see section 1.",
            _   => "Look this code up before assuming the device is absent: the app cannot tell "
                 + "'will not load' from 'not found', and reports both as a successful connect.",
        });
    }
}
