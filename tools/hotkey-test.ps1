# Live test: starts the real app minimised, presses the global hotkeys and checks that files appear.
param([string]$Exe = "$PSScriptRoot\..\src\Laqta\bin\Release\net10.0-windows\Laqta.exe")
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Keys {
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public static void CtrlPlus(byte vk) {
        keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }
}
'@
$dir = Join-Path ([Environment]::GetFolderPath('MyVideos')) 'Laqta'
$before = @(); if (Test-Path $dir) { $before = @(Get-ChildItem $dir | Select-Object -ExpandProperty FullName) }
$app = Start-Process $Exe -ArgumentList '--minimized' -PassThru
Start-Sleep 10
[Keys]::CtrlPlus(0x7B); Start-Sleep 2      # Ctrl+F12  screenshot
[Keys]::CtrlPlus(0x79); Start-Sleep 5      # Ctrl+F10  save replay
[Keys]::CtrlPlus(0x78); Start-Sleep 6      # Ctrl+F9   start recording
[Keys]::CtrlPlus(0x78); Start-Sleep 5      # Ctrl+F9   stop recording
$ffBefore = @(Get-Process ffmpeg -ErrorAction SilentlyContinue).Count
Stop-Process -Id $app.Id -Force
Start-Sleep 2
$ffAfter = @(Get-Process ffmpeg -ErrorAction SilentlyContinue).Count
"ffmpeg processes while running: $ffBefore, after killing the app: $ffAfter"
$new = @(Get-ChildItem $dir | Where-Object { $before -notcontains $_.FullName })
$new | ForEach-Object { "{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
$new | ForEach-Object { Remove-Item $_.FullName -Force }
"created and removed $($new.Count) test file(s)"
