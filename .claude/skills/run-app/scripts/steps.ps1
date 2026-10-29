<#
  Run a sequence of UI steps against OES_Leak_Monitor (and optionally Test_SECS). See ../SKILL.md.

  drive.ps1 answers "what does this tab look like"; this one does what needs several actions
  in order — sign in, connect, start a replay, fill a dialog, tick a row, screenshot several
  tabs. Steps are one string, ';'-separated, each 'verb|arg|arg':

    launch|OES          kill|OES          login           sleep|5
    tab|OES|Replay      press|OES|Play    toggle|OES|Raise leak*
    edits|OES           edit|OES|3|330    texts|OES|ALARM   buttons|TS
    rows|OES            row|OES|*13:49*   tick|OES|SIMP_OES1_*
    dlg-text            dlg-edit|Name     dlg-toggle|I confirm*     dlg-press|Start Capture
    shot|name           shot-dlg|name
    radio|TS|Equipment simulator (passive)                          profile|TS|EapForwarder

  App is OES or TS. Patterns are PowerShell -like wildcards on the visible text. Lines that
  start with '!!' are failures: a control not found or disabled — the step did nothing.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $Steps,
  [string] $OesExe = 'C:\Users\infor\source\repos\Ray1962\OES_Leak_Monitor\src\OES_Leak_Monitor\bin\Debug\net8.0-windows\OES_Leak_Monitor.exe',
  [string] $TsExe  = 'C:\Users\infor\source\repos\Test_SECS\bin\Debug\net10.0-windows\Test_SECS.exe',
  [string] $OutDir = "$env:TEMP\oeslm-shots",
  [int]    $Width  = 1200            # screenshots are scaled down to this; the manual uses 1200
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
Add-Type @"
using System;using System.Runtime.InteropServices;
public class StepWin {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int cmd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr hdc,uint flags);
  public struct RECT { public int Left,Top,Right,Bottom; }
}
"@
New-Item -ItemType Directory -Force $OutDir | Out-Null

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$exes  = @{ OES = $OesExe; TS = $TsExe }
$names = @{ OES = 'OES_Leak_Monitor'; TS = 'Test_SECS' }

function Cond($p, $v) { New-Object System.Windows.Automation.PropertyCondition($p, $v) }
function Proc($app) {
  $p = Get-Process $names[$app] -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($p) { $p.Refresh() }
  $p
}
function Root($app) { $AE::FromHandle((Proc $app).MainWindowHandle) }
function All($el, $type) { $el.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $type)) }
function Find($el, $type, $pat) {
  foreach ($x in (All $el $type)) { if ($x.Current.Name -like $pat) { return $x } }
  $null
}

# A WPF dialog is a child of the main window in the UIA tree, not a second top-level window.
function Dialog {
  $main = Root OES
  $d = $main.FindFirst($TS::Children, (Cond $AE::ControlTypeProperty $CT::Window))
  if (-not $d) { Write-Host '!! no dialog open' }
  $d
}

# Rows have no useful Name (it is the view-model type), so they are matched by their cells' text.
function Rows($app) {
  $r = Root $app
  @((All $r $CT::ListItem) + (All $r $CT::DataItem))
}
function RowText($row) { (All $row $CT::Text | ForEach-Object { $_.Current.Name }) -join ' | ' }

function Press($el, $pat, $label) {
  $b = Find $el $CT::Button $pat
  if (-not $b) { Write-Host "!! $label button not found: $pat"; return }
  if (-not $b.Current.IsEnabled) { Write-Host "!! $label button disabled: $($b.Current.Name)"; return }
  $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Write-Host "$label pressed $($b.Current.Name)"
}

# UIA Toggle does not raise a CheckBox's Click event. A dialog that enables its OK button from
# Click (GoldenRunCaptureDialog does) stays disabled — set a text box afterwards to re-evaluate.
function Toggle($el, $pat, $label) {
  $c = Find $el $CT::CheckBox $pat
  if (-not $c) { Write-Host "!! $label checkbox not found: $pat"; return }
  $t = $c.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  $t.Toggle()
  Write-Host "$label toggled $($c.Current.Name) -> $($t.Current.ToggleState)"
}

function SetValue($el, $value) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }

# PrintWindow rather than a screen copy: it captures the window even when something covers it.
function Shot($hwnd, $name) {
  $r = New-Object StepWin+RECT
  [StepWin]::GetWindowRect($hwnd, [ref]$r) | Out-Null
  $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
  $full = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($full)
  $hdc = $g.GetHdc(); [StepWin]::PrintWindow($hwnd, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
  $nw = [Math]::Min($Width, $w); $nh = [int]($h * $nw / $w)
  $out = New-Object System.Drawing.Bitmap $nw, $nh
  $g = [System.Drawing.Graphics]::FromImage($out)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.DrawImage($full, 0, 0, $nw, $nh); $g.Dispose()
  $path = Join-Path $OutDir "$name.png"
  $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $full.Dispose(); $out.Dispose()
  Write-Host "shot $path ${nw}x$nh"
}

# Credentials come from the environment, never from this file: the repo is shared and the
# password is a site's. Set OESLM_USER / OESLM_PASSWORD before running.
function Login {
  if (-not $env:OESLM_USER) { Write-Host '!! set OESLM_USER and OESLM_PASSWORD first'; return }
  Press (Root OES) 'Sign In' 'OES'
  Start-Sleep 2
  $box = (Dialog).FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Edit))
  $box.SetFocus(); Start-Sleep -Milliseconds 300
  [System.Windows.Forms.SendKeys]::SendWait('^a')
  [System.Windows.Forms.SendKeys]::SendWait("$($env:OESLM_USER){TAB}$($env:OESLM_PASSWORD){ENTER}")
  Start-Sleep 2
  Write-Host "signed in as $env:OESLM_USER"
}

foreach ($step in ($Steps -split ';')) {
  $verb, $a, $b, $c = $step -split '\|'
  switch ($verb) {
    'launch' { Start-Process $exes[$a] | Out-Null; Start-Sleep 9; [StepWin]::ShowWindow((Proc $a).MainWindowHandle, 3) | Out-Null; Write-Host "launched $a" }
    'kill'   { Get-Process $names[$a] -ErrorAction SilentlyContinue | Stop-Process -Force; Write-Host "killed $a" }
    'login'  { Login }
    'sleep'  { Start-Sleep -Seconds ([int]$a) }
    'tab'    { (Root $a).FindFirst($TS::Descendants, (Cond $AE::NameProperty $b)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Write-Host "$a tab $b" }
    'press'  { Press (Root $a) $b $a }
    'toggle' { Toggle (Root $a) $b $a }
    'radio'  { (Root $a).FindFirst($TS::Descendants, (Cond $AE::NameProperty $b)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Write-Host "$a radio $b" }
    'profile' {
      $cb = (Root $a).FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::ComboBox))
      $ec = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
      $ec.Expand(); Start-Sleep -Milliseconds 500
      $item = Find $cb $CT::ListItem "*$b*"
      if ($item) { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Write-Host "$a profile $($item.Current.Name)" } else { Write-Host "!! $a profile not found: $b" }
      $ec.Collapse()
    }
    # Text boxes have no name; 'edits' lists the on-screen ones with the index 'edit' takes.
    'edits'  { $i = 0; foreach ($e in (All (Root $a) $CT::Edit)) { if (-not $e.Current.IsOffscreen) { $v = ''; try { $v = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}; Write-Host "[$i] '$(($v -split "`n")[0])'" }; $i++ } }
    'edit'   { SetValue @(All (Root $a) $CT::Edit)[[int]$b] $c; Write-Host "$a edit[$b]=$c" }
    'buttons' { foreach ($x in (All (Root $a) $CT::Button)) { if (-not $x.Current.IsOffscreen) { Write-Host "BUTTON: $($x.Current.Name)" } } }
    'texts'  { foreach ($x in (All (Root $a) $CT::Text)) { if ($x.Current.Name -match $b) { Write-Host "TEXT: $($x.Current.Name)" } } }
    'rows'   { $i = 0; foreach ($r in (Rows $a)) { Write-Host "[$i] $(RowText $r)"; $i++ } }
    'row'    { $hit = $false; foreach ($r in (Rows $a)) { $t = RowText $r; if ($t -like $b) { $r.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Write-Host "selected $t"; $hit = $true; break } }; if (-not $hit) { Write-Host "!! no row like $b" } }
    'tick'   { $hit = $false; foreach ($r in (Rows $a)) { $t = RowText $r; if ($t -like $b) { Toggle $r '*' "row $t"; $hit = $true; break } }; if (-not $hit) { Write-Host "!! no row like $b" } }
    'dlg-text'   { $d = Dialog; if ($d) { Write-Host "DIALOG: $($d.Current.Name)"; foreach ($x in (All $d $CT::Text)) { Write-Host "  $($x.Current.Name)" } } }
    'dlg-edit'   { $d = Dialog; if ($d) { SetValue $d.FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Edit)) $a; Write-Host "dialog edit=$a" } }
    'dlg-toggle' { $d = Dialog; if ($d) { Toggle $d $a 'dialog' } }
    'dlg-press'  { $d = Dialog; if ($d) { Press $d $a 'dialog' } }
    'shot'     { Shot (Proc OES).MainWindowHandle $a }
    'shot-dlg' { $d = Dialog; if ($d) { Shot ([IntPtr]$d.Current.NativeWindowHandle) $a } }
    default  { Write-Host "!! unknown step: $verb" }
  }
}
