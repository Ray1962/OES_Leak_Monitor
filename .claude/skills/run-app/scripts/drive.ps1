<#
  Drive OES_Leak_Monitor's window from a WSL shell. See ../SKILL.md.

  Launches or attaches, maximises, enumerates tabs and buttons (name, size, position,
  enabled), optionally selects a tab and presses a button, then screenshots the window.

  UI Automation rather than coordinate clicking: the enumeration is often the whole
  answer, and it survives the window moving or a second monitor giving negative
  coordinates.
#>
[CmdletBinding()]
param(
  [string] $Exe = 'C:\Users\infor\source\repos\Ray1962\OES_Leak_Monitor\src\OES_Leak_Monitor\bin\Debug\net8.0-windows\OES_Leak_Monitor.exe',
  [string] $Tab,                      # tab to select, e.g. Logs
  [string] $Press,                    # button to invoke, by its visible text
  [string] $Png = "$env:TEMP\oeslm.png",
  [int]    $StartSeconds   = 9,       # WPF start-up plus the settings load
  [int]    $SettleSeconds  = 6,       # after -Press; background work needs more
  [switch] $KeepOpen                  # leave it running (it locks the .exe -- see SKILL.md)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @"
using System;using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int cmd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr hdc,uint flags);
  public struct RECT { public int Left,Top,Right,Bottom; }
}
"@

function Get-App {
  $p = Get-Process OES_Leak_Monitor -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $p) {
    if (-not (Test-Path $Exe)) { throw "No app process and no exe at $Exe -- build first." }
    Write-Host "Launching $Exe"
    Start-Process -FilePath $Exe
    Start-Sleep -Seconds $StartSeconds
    $p = Get-Process OES_Leak_Monitor -ErrorAction SilentlyContinue | Select-Object -First 1
  } else { Write-Host "Attaching to running instance (pid $($p.Id))" }
  if (-not $p) { throw "The app did not start." }
  $p
}

function Find-All($root,$type) {
  $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,$type)))
}

$app = Get-App
$h = $app.MainWindowHandle
[Win]::ShowWindow($h,3) | Out-Null          # maximise, so nothing is clipped
[Win]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 800

$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)

$tabs = Find-All $root ([System.Windows.Automation.ControlType]::TabItem)
Write-Host ("Tabs: " + (($tabs | ForEach-Object { $_.Current.Name }) -join ' | '))

if ($Tab) {
  $t = $tabs | Where-Object { $_.Current.Name -eq $Tab } | Select-Object -First 1
  if (-not $t) { Write-Host "!! tab not found: $Tab"; exit 1 }
  $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 1200
  Write-Host "Selected tab: $Tab"
}

# Enumerated after the tab switch, so this lists what is actually on screen. Often the
# whole answer -- size, position and enabled state, without an image.
foreach ($b in (Find-All $root ([System.Windows.Automation.ControlType]::Button))) {
  if (-not $b.Current.Name) { continue }
  $r = $b.Current.BoundingRectangle
  Write-Host ("Button: '{0}'  {1}x{2} at {3},{4}  enabled={5}" -f `
    $b.Current.Name,[int]$r.Width,[int]$r.Height,[int]$r.X,[int]$r.Y,$b.Current.IsEnabled)
}

if ($Press) {
  $btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
         (New-Object System.Windows.Automation.PropertyCondition(
           [System.Windows.Automation.AutomationElement]::NameProperty,$Press)))
  if (-not $btn) { Write-Host "!! button not found: $Press"; exit 1 }
  $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Write-Host "Invoked: $Press  (settling ${SettleSeconds}s)"
  Start-Sleep -Seconds $SettleSeconds
  [Win]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 700
}

$rect = New-Object Win+RECT
[Win]::GetWindowRect($h,[ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left; $ht = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap $w,$ht
$g = [System.Drawing.Graphics]::FromImage($bmp)

# PrintWindow, not CopyFromScreen. SetForegroundWindow is advisory -- Windows refuses to
# let a background process steal focus, so CopyFromScreen happily returns whatever is
# actually on top of that rectangle. It did exactly that once here and handed back a
# screenshot of the editor, which is worse than a blank frame: it looks like a result.
# PW_RENDERFULLCONTENT (2) asks the window to render itself, occluded or not.
$hdc = $g.GetHdc()
$ok = [Win]::PrintWindow($h,$hdc,2)
$g.ReleaseHdc($hdc)
if (-not $ok) {
  Write-Host "PrintWindow failed; falling back to a screen grab (may capture whatever is on top)"
  $g.CopyFromScreen($rect.Left,$rect.Top,0,0,$bmp.Size)   # negative coords are fine
}
$bmp.Save($Png,[System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host ("Screenshot {0}x{1} -> {2}" -f $w,$ht,$Png)

if (-not $KeepOpen) {
  Stop-Process -Id $app.Id -Force
  Write-Host "Closed (it locks the .exe; a build would fail with MSB3027)."
}
