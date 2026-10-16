# Smoke test for Taskbar Monitor. Builds a copy of the sources, starts it and checks the Settings window.
#
#   powershell -ExecutionPolicy Bypass -File tests\smoke.ps1
#
# Close Taskbar Monitor first (only one copy can run at a time). Your settings in
# HKCU\Software\TaskbarMonitor are saved and restored, and your own TaskbarMonitor.exe is not touched.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$keyPath = 'Software\TaskbarMonitor'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SmokeWin {
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
  [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
  // choose an item the way a click does: CB_SETCURSEL, then tell the parent (WM_COMMAND / CBN_SELCHANGE)
  public static void Choose(IntPtr combo, int index) {
    SendMessage(combo, 0x014E, (IntPtr)index, IntPtr.Zero);
    SendMessage(GetParent(combo), 0x0111, (IntPtr)((1 << 16) | (GetDlgCtrlID(combo) & 0xFFFF)), combo);
  }
  public static int Find(IntPtr combo, string text) { return (int)SendMessage(combo, 0x0158, (IntPtr)(-1), text); } // CB_FINDSTRINGEXACT
}
"@

$failed = 0
function Check($name, $ok) { if ($ok) { "PASS  $name" } else { "FAIL  $name"; $script:failed++ } }
function Slots() { (Get-ItemProperty "HKCU:\$keyPath" -ErrorAction SilentlyContinue).Slots }

if (Get-Process TaskbarMonitor -ErrorAction SilentlyContinue) { throw "Close Taskbar Monitor first (right-click a widget, Exit)." }

# build a copy so the exe in the repository is left alone
$tmp = Join-Path $env:TEMP ("tm_smoke_" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $tmp | Out-Null
Copy-Item "$root\src", "$root\build.bat" $tmp -Recurse
Push-Location $tmp; & ".\build.bat" | Out-Null; Pop-Location
$exe = "$tmp\TaskbarMonitor.exe"
Check "build" (Test-Path $exe)

# save the user's settings
$saved = $null
$k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyPath)
if ($k) { $saved = @($k.GetValueNames() | % { @{ N = $_; V = $k.GetValue($_); T = $k.GetValueKind($_) } }); $k.Close() }

function Open-Settings() {
    $p = Start-Process $exe -ArgumentList '--settings' -PassThru
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), 'Taskbar Monitor - Settings'
    $win = $null
    1..20 | % { if (-not $win) { Start-Sleep -m 500; $win = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond) } }
    return @{ Process = $p; Window = $win; Cond = $cond }
}
function Combos($win) {   # the six drop-downs, in screen order: row by row, left to right
    # the class name ends with a build-dependent suffix (..._r6_ad1), so match only the control type
    $all = $win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    @($all | ? { $_.Current.ClassName -like '*COMBOBOX*' } | % { $b = $_.Current.BoundingRectangle; [pscustomobject]@{ H = [IntPtr]$_.Current.NativeWindowHandle; X = $b.X; Y = $b.Y } }) | sort Y, X
}

try {
    # 1) damaged settings must not stop the app: invalid Slots, text where numbers are expected
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($keyPath)
    foreach ($n in $key.GetValueNames()) { $key.DeleteValue($n) }
    $key.SetValue('Slots', '99,Ram,CpuTemp,None,Down,Up'); $key.SetValue('MY', 'abc'); $key.SetValue('MR', 'xyz')
    $key.SetValue('Hidden', 'no'); $key.SetValue('Media', 'maybe'); $key.Close()
    $s = Open-Settings
    Check "starts with damaged settings and opens Settings" ($s.Window -ne $null -and -not $s.Process.HasExited)
    if (-not $s.Window) { throw "Settings window did not open" }

    # 2) choose a value: it is saved
    $c = Combos $s.Window       # [0]=col1 top, [1]=col2 top, [2]=col3 top, [3]=col1 bottom, ...
    $top1 = $c[0].H
    [SmokeWin]::Choose($top1, [SmokeWin]::Find($top1, 'Disk activity')); Start-Sleep -m 700
    Check "choosing 'Disk activity' saves it" ((Slots) -match '^Disk,')

    # 3) clicking a category heading selects its first item
    [SmokeWin]::Choose($top1, [SmokeWin]::Find($top1, 'GPU')); Start-Sleep -m 700
    Check "clicking the 'GPU' heading selects 'GPU usage'" ((Slots) -match '^Gpu,')

    # 4) a value can only appear once: choosing one that is used elsewhere swaps the two positions
    $other = ((Slots) -split ',')[1]                                  # value of slot 1
    $name = @{ Ram = 'RAM usage (%)'; None = '(empty)' }[$other]
    if ($name) {
        [SmokeWin]::Choose($top1, [SmokeWin]::Find($top1, $name)); Start-Sleep -m 700
        $parts = (Slots) -split ','
        $used = @($parts | ? { $_ -ne 'None' })
        Check "no value appears twice after a swap" ($used.Count -eq @($used | select -Unique).Count)
    }

    # 5) Close closes the window and the app keeps running
    $btn = $s.Window.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), 'Close'))
    [void][SmokeWin]::SendMessage([IntPtr]$btn.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)   # BM_CLICK
    Start-Sleep 1
    $gone = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $s.Cond) -eq $null
    Check "Close closes the Settings window" $gone
    Check "app still running after Close" (-not $s.Process.HasExited)
}
finally {
    Get-Process TaskbarMonitor -ErrorAction SilentlyContinue | Stop-Process -Force
    # restore the user's settings
    try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($keyPath, $false) } catch { }
    if ($saved) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($keyPath)
        foreach ($v in $saved) { $key.SetValue($v.N, $v.V, $v.T) }
        $key.Close()
    }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { "`n$failed check(s) FAILED"; exit 1 } else { "`nAll checks passed" }
