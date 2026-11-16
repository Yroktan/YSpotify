param([switch]$Resize,[switch]$Login,[string]$ProcessName='YSpotify')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;using System.Text;using System.Runtime.InteropServices;
public class YSpotifyCheck {
 public delegate bool EnumProc(IntPtr h,IntPtr p);
 [StructLayout(LayoutKind.Sequential)]public struct Rect{public int L,T,R,B;}
 [DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
 [DllImport("user32.dll")]public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int z,bool r);
 [DllImport("user32.dll")]public static extern bool EnumChildWindows(IntPtr h,EnumProc f,IntPtr p);
 [DllImport("user32.dll")]public static extern int GetWindowText(IntPtr h,StringBuilder t,int n);
 [DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int n);
 [DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint m,IntPtr a,IntPtr b);
}
'@
$clientProcess=Get-Process -Name $ProcessName
if($Resize){
 foreach($size in @(@(896,679),@(1116,779),@(1456,939),@(1256,879))){
  [void][YSpotifyCheck]::MoveWindow($clientProcess.MainWindowHandle,150,100,$size[0],$size[1],$true)
  Start-Sleep -Milliseconds 180
  if(-not (Get-Process -Id $clientProcess.Id).Responding){throw 'Window is unresponsive during resize'}
 }
 [void][YSpotifyCheck]::ShowWindow($clientProcess.MainWindowHandle,3)
 Start-Sleep -Milliseconds 200
 [void][YSpotifyCheck]::ShowWindow($clientProcess.MainWindowHandle,9)
}
$rect=New-Object YSpotifyCheck+Rect
[void][YSpotifyCheck]::GetWindowRect($clientProcess.MainWindowHandle,[ref]$rect)
$bitmap=New-Object Drawing.Bitmap ($rect.R-$rect.L),($rect.B-$rect.T)
$graphics=[Drawing.Graphics]::FromImage($bitmap)
$dc=$graphics.GetHdc()
[void][YSpotifyCheck]::PrintWindow($clientProcess.MainWindowHandle,$dc,2)
$graphics.ReleaseHdc($dc)
$bitmap.Save((Join-Path $PSScriptRoot 'yspotify-check.png'))
$graphics.Dispose();$bitmap.Dispose()
[void][YSpotifyCheck]::EnumChildWindows($clientProcess.MainWindowHandle,{
 param($h,$p)
 $t=New-Object Text.StringBuilder 1024
 [void][YSpotifyCheck]::GetWindowText($h,$t,1024)
 if($Login -and $t.ToString().StartsWith([string]::Concat([char[]]@(0x412,0x43E,0x439,0x442,0x438)))){[void][YSpotifyCheck]::PostMessage($h,0xF5,[IntPtr]::Zero,[IntPtr]::Zero)}
 if($t.Length -gt 0){Write-Host $t.ToString()}
 return $true
},[IntPtr]::Zero)
$snapshotPath=Join-Path $env:LOCALAPPDATA 'YSpotify\runtime-metrics.json'
$snapshot=if(Test-Path -LiteralPath $snapshotPath){[IO.File]::ReadAllText($snapshotPath)|ConvertFrom-Json}else{$null}
$ids=if($snapshot -and $snapshot.clientProcessId -eq $clientProcess.Id){@($snapshot.processIds)}else{@($clientProcess.Id)}
$metrics=Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | Where-Object {$_.IDProcess -in $ids}
$metrics | Select-Object Name,IDProcess,@{N='PrivateWS_MB';E={[math]::Round($_.WorkingSetPrivate/1MB,1)}},@{N='WS_MB';E={[math]::Round($_.WorkingSet/1MB,1)}},@{N='Commit_MB';E={[math]::Round($_.PrivateBytes/1MB,1)}} | Format-Table
[pscustomobject]@{TotalPrivateWS_MB=[math]::Round(($metrics | Measure-Object WorkingSetPrivate -Sum).Sum/1MB,1);TotalWS_MB=[math]::Round(($metrics | Measure-Object WorkingSet -Sum).Sum/1MB,1)} | Format-List
