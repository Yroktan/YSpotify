param([switch]$SourceOnly)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$playerFiles=@('player/main.cjs','player/service.cjs','player/auth.cjs','player/catalog.cjs','player/discord.cjs','player/config.json','player/audio-worker.js','player/session-capture.js','player/SOURCES.md')
$brandingFiles=@('branding/utsuho-outline-v2.png','branding/SOURCE.txt','branding/yspotify.ico','branding/yspotify-avatar-v1.png')
$sourceFiles=@('AssemblyInfo.cs','App.cs','Design.cs','LibraryUI.cs','NativeBridge.cs','Memory.cs','BrandHeader.cs','DesignReview.cs','EngineLifecycle.cs','FirefoxBackend.cs','MiniBackend.cs','SmokeTests.cs','MiniAcceptance.cs','bridge.js','build.ps1','test.ps1','test-mini.ps1','check-window.ps1','prepare-player.ps1','package.ps1','tools/build-icon.ps1','YSpotify.exe.config','README.md','SPEC.md','PERFORMANCE.md','.gitignore')+$playerFiles+$brandingFiles
$portableFiles=@('YSpotify.exe','YSpotify.exe.config','bridge.js','README.md','PERFORMANCE.md','THIRD_PARTY_NOTICES.txt')+$playerFiles+$brandingFiles
function Write-Archive([string]$Name,[string[]]$Files,[string]$Prefix){
 $target=Join-Path $PSScriptRoot $Name
 $stream=[IO.File]::Open($target,[IO.FileMode]::Create)
 $archive=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create)
 try{
  foreach($file in $Files){
   $full=Join-Path $PSScriptRoot $file
   if(-not (Test-Path -LiteralPath $full -PathType Leaf)){throw ('Missing file: '+$file)}
   [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$full,($Prefix+$file.Replace('\','/')),[IO.Compression.CompressionLevel]::Optimal)
  }
 }finally{$archive.Dispose();$stream.Dispose()}
 Get-Item -LiteralPath $target | Select-Object Name,Length
}
$sourceFiles+=@('player/service.test.cjs','player/auth.test.cjs','player/catalog.test.cjs','validation/quick-check.json','validation/endurance-check.json','validation/latency-check.json','THIRD_PARTY_NOTICES.txt')
$sourceFiles+=@('build-android.ps1','.github/workflows/android.yml','ci/android-ui.sh','android/README.md','android/build.gradle','android/settings.gradle','android/gradle.properties','android/gradlew','android/gradlew.bat','android/gradle/wrapper/gradle-wrapper.jar','android/gradle/wrapper/gradle-wrapper.properties','android/app/build.gradle','android/app/proguard-rules.pro','android/app/src/native/AndroidManifest.xml')
$sourceFiles+=@('android/design/portrait.png','android/design/player.png','android/design/landscape.png','android/design/portrait-large-text.png','android/design/player-large-text.png','android/design/landscape-large-text.png','validation/android-check.json')
foreach($directory in @('android/app/src/main','android/app/src/native/java','android/app/src/test','android/app/src/androidTest')){
 $sourceFiles+=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $directory) -Recurse -File | Where-Object {$_.Extension -in @('.java','.xml','.png','.json','.txt')} | ForEach-Object {$_.FullName.Substring($PSScriptRoot.Length+1).Replace('\','/')})
}
Write-Archive 'YSpotify-source.zip' $sourceFiles ''
if(-not $SourceOnly){
 $runtime=Join-Path $PSScriptRoot 'runtime/ECS'
 if(-not (Test-Path (Join-Path $runtime 'electron.exe.sig'))){throw 'Signed runtime is required'}
 $runtimeFiles=Get-ChildItem -LiteralPath $runtime -Recurse -File | ForEach-Object {$_.FullName.Substring($PSScriptRoot.Length+1).Replace('\','/')}
 Write-Archive 'YSpotify-portable.zip' ($portableFiles+@($runtimeFiles)) 'YSpotify/'
}
