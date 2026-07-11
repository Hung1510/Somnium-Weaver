<#
  record-demo.ps1
  Fires Somnium Weaver's global hotkeys on the DEMO.md shot-list schedule so
  every recording take is timed identically. Start OBS (or ScreenToGif) FIRST,
  then run this script, then stop recording ~1s after it finishes.

  Cue schedule (matches docs/DEMO.md):
    t=0    Ctrl+Alt+D   HUD on               (calm motes begin)
    t=3    Ctrl+Alt+B   manual burst
    t=5    Ctrl+Alt+A   audio-reactive on    <-- start your music track here too
    t=9    Ctrl+Alt+S   click-through toggle (drag a window "through" it now)
    t=12   end of clip

  Usage:
    1. Launch SomniumWeaver.exe manually first, position/settle the window.
    2. Start your screen recorder.
    3. Run:  powershell -ExecutionPolicy Bypass -File .\record-demo.ps1
    4. When it prints "DONE", stop the recorder a beat later.
#>

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class KeySim {
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_MENU    = 0x12; // Alt
}
"@

function Send-CtrlAlt([byte]$vkKey, [string]$label) {
    Write-Host "  -> Ctrl+Alt+$label"
    [KeySim]::keybd_event([KeySim]::VK_CONTROL, 0, 0, [UIntPtr]::Zero)
    [KeySim]::keybd_event([KeySim]::VK_MENU, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [KeySim]::keybd_event($vkKey, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [KeySim]::keybd_event($vkKey, 0, [KeySim]::KEYEVENTF_KEYUP, [UIntPtr]::Zero)
    [KeySim]::keybd_event([KeySim]::VK_MENU, 0, [KeySim]::KEYEVENTF_KEYUP, [UIntPtr]::Zero)
    [KeySim]::keybd_event([KeySim]::VK_CONTROL, 0, [KeySim]::KEYEVENTF_KEYUP, [UIntPtr]::Zero)
}

$VK_D = 0x44; $VK_B = 0x42; $VK_A = 0x41; $VK_S = 0x53

Write-Host "Recording cue sequence starts in:"
3..1 | ForEach-Object { Write-Host "  $_..."; Start-Sleep -Seconds 1 }

$sw = [Diagnostics.Stopwatch]::StartNew()
Write-Host "t=0   GO — start your recorder's clock reference now"
Send-CtrlAlt $VK_D "D  (HUD on)"

while ($sw.Elapsed.TotalSeconds -lt 3) { Start-Sleep -Milliseconds 50 }
Write-Host "t=3"
Send-CtrlAlt $VK_B "B  (manual burst)"

while ($sw.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 50 }
Write-Host "t=5   <-- start your music track NOW"
Send-CtrlAlt $VK_A "A  (audio-reactive on)"

while ($sw.Elapsed.TotalSeconds -lt 9) { Start-Sleep -Milliseconds 50 }
Write-Host "t=9   <-- drag a window across the app now"
Send-CtrlAlt $VK_S "S  (click-through toggle)"

while ($sw.Elapsed.TotalSeconds -lt 12) { Start-Sleep -Milliseconds 50 }
Write-Host "t=12  DONE — stop the recorder in ~1s"
