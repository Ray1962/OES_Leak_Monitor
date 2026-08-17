<#
    check-oes-connect.ps1 - why does OES_Leak_Monitor fall back to test mode?

    Reproduces exactly what the app does at Connect (DllResolver.Resolve ->
    CheckHardwareDllAvailability -> USB enumeration) and prints the reason the app
    itself swallows. Run it in the SAME folder as OES_Leak_Monitor.exe, as the SAME
    Windows user, with the spectrometer plugged in and every other OES program closed.

        powershell -ExecutionPolicy Bypass -File .\check-oes-connect.ps1
#>
param([string]$Folder = $PSScriptRoot)

$ErrorActionPreference = 'Continue'
function Line { Write-Host ('-' * 66) }

Write-Host "OES connect diagnostic  -  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Line
Write-Host "Folder        : $Folder"
Write-Host "PowerShell    : $($PSVersionTable.PSVersion)  64-bit process: $([Environment]::Is64BitProcess)"
Write-Host "User          : $env:USERNAME"
if (-not [Environment]::Is64BitProcess) {
    Write-Host "!! This is a 32-bit PowerShell. The app is x64 - rerun with the 64-bit one:" -ForegroundColor Red
    Write-Host "   C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -ForegroundColor Red
    return
}

# ---------------------------------------------------------------- 1. files
Line
Write-Host "1. Files that must sit next to the .exe"
$oesNative = @('OES_Leak_Monitor.exe','UserApplication.dll','SiUSBXp.dll','libsodium.dll',
               'vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll')
$wpfNative = @('wpfgfx_cor3.dll','PresentationNative_cor3.dll','D3DCompiler_47_cor3.dll',
               'PenImc_cor3.dll','vcruntime140_cor3.dll')
$missing = @()
$wpfMissing = @()
foreach ($f in ($oesNative + $wpfNative)) {
    $p = Join-Path $Folder $f
    if (Test-Path $p) {
        $len = (Get-Item $p).Length
        $blocked = if (Get-Item -Stream Zone.Identifier -Path $p -ErrorAction SilentlyContinue) { '  [BLOCKED - run Unblock-File]' } else { '' }
        Write-Host ("   OK      {0,-32} {1,10:N0}{2}" -f $f, $len, $blocked)
    } elseif ($oesNative -contains $f) {
        $missing += $f
        Write-Host ("   MISSING {0}   <- required, this alone causes test mode" -f $f) -ForegroundColor Red
    } else {
        $wpfMissing += $f
        Write-Host ("   absent  {0}   (only needed in a self-contained publish folder)" -f $f) -ForegroundColor Yellow
    }
}

# ------------------------------------------------------- 2. VC++ runtime
Line
Write-Host "2. Visual C++ runtime (libsodium.dll imports VCRUNTIME140.dll)"
foreach ($rt in @('vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll')) {
    $inFolder = Test-Path (Join-Path $Folder $rt)
    $inSystem = Test-Path (Join-Path $env:SystemRoot "System32\$rt")
    $where = @()
    if ($inFolder) { $where += 'app folder' }
    if ($inSystem) { $where += 'System32' }
    $state  = if ($where.Count) { $where -join ' + ' } else { 'NOT FOUND' }
    $colour = if ($where.Count) { 'Gray' } else { 'Red' }
    Write-Host ("   {0,-20} {1}" -f $rt, $state) -ForegroundColor $colour
}
Write-Host "   (vcruntime140_cor3.dll is WPF's private copy and does NOT count)"

# ------------------------------------------------------------- 3. bitness
Line
Write-Host "3. UserApplication.dll bitness"
$dll = Join-Path $Folder 'UserApplication.dll'
if (Test-Path $dll) {
    $fs = [IO.File]::OpenRead($dll); $br = New-Object IO.BinaryReader($fs)
    $fs.Position = 0x3c; $pe = $br.ReadInt32(); $fs.Position = $pe + 4
    $machine = $br.ReadUInt16(); $br.Close()
    $arch = switch ($machine) { 0x8664 {'x64'} 0x14c {'x86 - WRONG, app is x64'} default {"0x{0:X}" -f $machine} }
    Write-Host "   machine = $arch"
} else { Write-Host "   skipped (file missing)" -ForegroundColor Red }

# --------------------------------------------------------- 4. native load
Line
Write-Host "4. Native load test (this is what CheckHardwareDllAvailability does)"
Add-Type -Namespace Oes -Name Nat -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
public static extern IntPtr LoadLibraryExW(string path, IntPtr h, uint flags);
[DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
public static extern bool SetDllDirectoryW(string path);
'@
[void][Oes.Nat]::SetDllDirectoryW($Folder)
$loadOk = $true
foreach ($f in @('SiUSBXp.dll','libsodium.dll','UserApplication.dll')) {
    $p = Join-Path $Folder $f
    if (-not (Test-Path $p)) { continue }
    $h = [Oes.Nat]::LoadLibraryExW($p, [IntPtr]::Zero, 0x8)   # LOAD_WITH_ALTERED_SEARCH_PATH
    if ($h -ne [IntPtr]::Zero) {
        Write-Host ("   loaded  {0}" -f $f)
    } else {
        $loadOk = $false
        $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $hint = switch ($err) {
            126 { 'ERROR_MOD_NOT_FOUND - a DLL it depends on is missing (usually the VC++ 2015-2022 x64 redistributable)' }
            193 { 'ERROR_BAD_EXE_FORMAT - 32/64-bit mismatch' }
            5   { 'ACCESS_DENIED - antivirus or file permissions' }
            default { (New-Object ComponentModel.Win32Exception($err)).Message }
        }
        Write-Host ("   FAILED  {0}  (Win32 {1}: {2})" -f $f, $err, $hint) -ForegroundColor Red
    }
}

# ------------------------------------------------------- 5. USB enumeration
Line
Write-Host "5. USB enumeration (UAI_SpectrometerGetDeviceList / GetDeviceAmount)"
if (-not $loadOk) {
    Write-Host "   skipped - the DLL did not load, so the app never gets this far." -ForegroundColor Yellow
} else {
    Add-Type -Namespace Oes -Name Uai -MemberDefinition @'
[DllImport("UserApplication.dll", CallingConvention=CallingConvention.Cdecl)]
public static extern uint UAI_SpectrometerGetDeviceList(ref uint BufferSize, uint[] VIDPID);
[DllImport("UserApplication.dll", CallingConvention=CallingConvention.Cdecl)]
public static extern uint UAI_SpectrometerGetDeviceAmount(uint VID, uint PID, ref uint NumDevices);
[DllImport("UserApplication.dll", CallingConvention=CallingConvention.Cdecl)]
public static extern uint UAI_SpectrometerOpen(uint dev, ref IntPtr handle, uint VID, uint PID);
[DllImport("UserApplication.dll", CallingConvention=CallingConvention.Cdecl)]
public static extern uint UAI_SpectrometerClose(IntPtr handle);
[DllImport("UserApplication.dll", CallingConvention=CallingConvention.Cdecl)]
public static extern uint UAI_SpectromoduleGetFrameSize(IntPtr handle, ref uint size);
'@
    try {
        $n = [uint32]0
        [void][Oes.Uai]::UAI_SpectrometerGetDeviceList([ref]$n, $null)
        Write-Host "   device types reported: $n"
        if ($n -eq 0) {
            Write-Host "   -> no VID/PID entries: driver not installed, or nothing plugged in." -ForegroundColor Red
        }
        $vidpid = New-Object uint32[] ($n * 2)
        [void][Oes.Uai]::UAI_SpectrometerGetDeviceList([ref]$n, $vidpid)
        $total = 0
        for ($j = 0; $j -lt $n * 2; $j += 2) {
            $cnt = [uint32]0
            [void][Oes.Uai]::UAI_SpectrometerGetDeviceAmount($vidpid[$j], $vidpid[$j+1], [ref]$cnt)
            Write-Host ("   VID={0:X4} PID={1:X4}  devices={2}" -f $vidpid[$j], $vidpid[$j+1], $cnt)
            $total += $cnt
            for ($i = 0; $i -lt $cnt; $i++) {
                $h = [IntPtr]::Zero
                $st = [Oes.Uai]::UAI_SpectrometerOpen($i, [ref]$h, $vidpid[$j], $vidpid[$j+1])
                if ($st -eq 0 -and $h -ne [IntPtr]::Zero) {
                    $fs = [uint32]0
                    [void][Oes.Uai]::UAI_SpectromoduleGetFrameSize($h, [ref]$fs)
                    Write-Host ("      open #{0}: OK, frame size = {1}" -f $i, $fs) -ForegroundColor Green
                    [void][Oes.Uai]::UAI_SpectrometerClose($h)
                } else {
                    Write-Host ("      open #{0}: FAILED status=0x{1:X} - in use by another program?" -f $i, $st) -ForegroundColor Red
                }
            }
        }
        if ($total -eq 0 -and $n -gt 0) {
            Write-Host "   -> device types known but 0 units attached." -ForegroundColor Red
        }
    } catch {
        Write-Host "   P/Invoke threw: $($_.Exception.Message)" -ForegroundColor Red
    }
}

Line
Write-Host "Verdict"
if ($missing.Count) { Write-Host "  * Files missing from the folder - ship the whole publish folder." -ForegroundColor Red }
if (-not $loadOk)   { Write-Host "  * The DLL will not load: the app lands in test mode with NO log entry." -ForegroundColor Red }
if ($loadOk)        { Write-Host "  * The DLL loads. If section 5 found 0 devices or open failed, that is the test-mode cause." }
Write-Host "Close every other OES program (SpectraSmart, a second copy of this app) and rerun if 'open FAILED'."
