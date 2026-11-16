$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$quickOnly=$args -contains '--quick-only'
$latencyOnly=$args -contains '--latency-only'
Push-Location $PSScriptRoot
try {
 & $compiler /nologo /codepage:65001 /target:exe /main:YSpotify.MiniAcceptance /platform:x64 /optimize+ /out:YSpotify.MiniTests.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Security.dll App.cs Design.cs LibraryUI.cs NativeBridge.cs Memory.cs BrandHeader.cs DesignReview.cs EngineLifecycle.cs FirefoxBackend.cs MiniBackend.cs MiniAcceptance.cs
 if($LASTEXITCODE -ne 0){throw 'Test compilation failed'}
 if($latencyOnly){& .\YSpotify.MiniTests.exe --latency-only}elseif($quickOnly){& .\YSpotify.MiniTests.exe --quick-only}else{& .\YSpotify.MiniTests.exe}
 if($LASTEXITCODE -ne 0){throw 'Native acceptance failed'}
} finally {Pop-Location}
