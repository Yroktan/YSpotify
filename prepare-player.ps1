$ErrorActionPreference='Stop'
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$runtime=Join-Path $PSScriptRoot 'runtime\ECS'
if(Test-Path -LiteralPath (Join-Path $runtime 'electron.exe.sig')){Write-Output 'Signed ECS runtime is already installed.';exit 0}
if(Test-Path -LiteralPath (Join-Path $runtime 'electron.exe')){throw 'ECS is present but needs production VMP signing. See player/SOURCES.md; the executable must not be modified after signing.'}
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('YSpotify-runtime-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
$archive=Join-Path $temporary 'ecs.zip'
$download='https://github.com/castlabs/electron-releases/releases/download/v45.2.1%2Bwvcus/electron-v45.2.1%2Bwvcus-win32-x64.zip'
$expected='a17f87a95a62e8f336d6fa18e57b9bc83d513b02211e4e5771a45cfd7482740e'
try{
 Invoke-WebRequest -Uri $download -OutFile $archive -UseBasicParsing
 if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected){throw 'Runtime archive SHA-256 mismatch'}
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 [void][IO.Directory]::CreateDirectory($runtime)
 [IO.Compression.ZipFile]::ExtractToDirectory($archive,$runtime)
 Write-Output 'Official ECS 45.2.1+wvcus installed. Production VMP signing by the build author is still required; see player/SOURCES.md.'
}finally{
 $resolved=[IO.Path]::GetFullPath($temporary)
 $allowed=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\YSpotify-runtime-'
 if($resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
