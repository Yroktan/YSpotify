param([string]$Output='YSpotify.exe')
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /codepage:65001 /target:winexe /main:YSpotify.Program /platform:x64 /optimize+ /win32icon:branding\yspotify.ico ("/out:"+$Output) /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Security.dll AssemblyInfo.cs App.cs Design.cs LibraryUI.cs NativeBridge.cs Memory.cs BrandHeader.cs DesignReview.cs EngineLifecycle.cs FirefoxBackend.cs MiniBackend.cs MiniAcceptance.cs
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
} finally { Pop-Location }
