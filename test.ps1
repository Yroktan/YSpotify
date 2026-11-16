$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$layoutOnly=$args -contains '--layout-only'
Push-Location $PSScriptRoot
try {
 & $compiler /nologo /codepage:65001 /target:exe /main:YSpotify.SmokeTests /platform:x64 /optimize+ /out:YSpotify.Tests.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Security.dll App.cs Design.cs LibraryUI.cs NativeBridge.cs Memory.cs BrandHeader.cs DesignReview.cs EngineLifecycle.cs FirefoxBackend.cs MiniBackend.cs MiniAcceptance.cs SmokeTests.cs
 if($LASTEXITCODE -ne 0){throw 'Test compilation failed'}
 if($layoutOnly){& .\YSpotify.Tests.exe --layout-only}else{& .\YSpotify.Tests.exe}
 if($LASTEXITCODE -ne 0){throw 'Smoke tests failed'}
} finally{Pop-Location}
