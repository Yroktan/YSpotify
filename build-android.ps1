param([ValidateSet('Debug','Release')][string]$Configuration='Release',[switch]$SkipChecks,[switch]$Native)
$ErrorActionPreference='Stop'
$tools=Join-Path $env:USERPROFILE 'YSpotifyTools'
if(-not $env:GRADLE_USER_HOME -and (Test-Path -LiteralPath (Join-Path $tools 'gradle-cache'))){$env:GRADLE_USER_HOME=Join-Path $tools 'gradle-cache'}
if(-not $env:JAVA_HOME -and (Test-Path -LiteralPath (Join-Path $tools 'java'))){$env:JAVA_HOME=(Get-ChildItem -LiteralPath (Join-Path $tools 'java') -Directory | Select-Object -First 1).FullName}
if(-not $env:ANDROID_HOME){
 if($env:ANDROID_SDK_ROOT){$env:ANDROID_HOME=$env:ANDROID_SDK_ROOT}
 elseif(Test-Path -LiteralPath (Join-Path $tools 'android-sdk')){$env:ANDROID_HOME=Join-Path $tools 'android-sdk'}
 else{$env:ANDROID_HOME=Join-Path $env:LOCALAPPDATA 'Android\Sdk'}
}
if(-not $env:JAVA_HOME -or -not (Test-Path -LiteralPath $env:ANDROID_HOME)){throw 'Install JDK 17 and Android SDK 36; set JAVA_HOME and ANDROID_HOME'}
$signing=Join-Path $env:LOCALAPPDATA 'YSpotifyBuild'
try{
 if($Configuration -eq 'Release'){
  Add-Type -AssemblyName System.Security
  [void][IO.Directory]::CreateDirectory($signing)
  $key=Join-Path $signing 'android-release.p12'
  $protected=Join-Path $signing 'android-keypass.bin'
  $entropy=[Text.Encoding]::UTF8.GetBytes('YSpotify.AndroidSigning.v1')
  if(Test-Path -LiteralPath $protected){$bytes=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($protected),$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)}
  else{
   if(Test-Path -LiteralPath $key){throw 'Signing password is missing; restore android-keypass.bin before building'}
   $bytes=New-Object byte[] 48;$random=[Security.Cryptography.RandomNumberGenerator]::Create();try{$random.GetBytes($bytes)}finally{$random.Dispose()}
   [IO.File]::WriteAllBytes($protected,[Security.Cryptography.ProtectedData]::Protect($bytes,$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser))
  }
  $env:YSPOTIFY_STORE_PASSWORD=[Convert]::ToBase64String($bytes)
  $env:YSPOTIFY_KEY_PASSWORD=$env:YSPOTIFY_STORE_PASSWORD
  $env:YSPOTIFY_KEYSTORE=$key
  $env:YSPOTIFY_KEY_ALIAS='yspotify'
  if(-not (Test-Path -LiteralPath $key)){
   & (Join-Path $env:JAVA_HOME 'bin\keytool.exe') -genkeypair -keystore $key -storetype PKCS12 -storepass:env YSPOTIFY_STORE_PASSWORD -keypass:env YSPOTIFY_KEY_PASSWORD -alias yspotify -keyalg RSA -keysize 4096 -sigalg SHA256withRSA -validity 10000 -dname CN=YSpotify
   if($LASTEXITCODE -ne 0){throw 'Signing key generation failed'}
  }
 }
 $tasks=@(':app:assemble'+$Configuration)
 if(-not $SkipChecks){
  if(-not $Native){$tasks+=':app:testDebugUnitTest'}
  $tasks+=(':app:lint'+$Configuration)
 }
 if($Native){$tasks+='-PyspotifyNative=true'}
 & (Join-Path $PSScriptRoot 'android\gradlew.bat') -p (Join-Path $PSScriptRoot 'android') @tasks --console plain
 if($LASTEXITCODE -ne 0){throw 'Android build or checks failed'}
 $variant=$Configuration.ToLowerInvariant()
 $apk=Join-Path $PSScriptRoot ('android\app\build\outputs\apk\'+$variant+'\app-'+$variant+'.apk')
 $version=if($Native){'0.2.0'}else{'0.1.0'}
 $target=Join-Path $PSScriptRoot ('YSpotify-Android-'+$version+$(if($Configuration -eq 'Debug'){'-debug'}else{''})+'.apk')
 Copy-Item -LiteralPath $apk -Destination $target -Force
 Get-FileHash -LiteralPath $target -Algorithm SHA256 | Select-Object Path,Hash
}finally{
 Remove-Item Env:YSPOTIFY_STORE_PASSWORD,Env:YSPOTIFY_KEY_PASSWORD,Env:YSPOTIFY_KEYSTORE,Env:YSPOTIFY_KEY_ALIAS -ErrorAction SilentlyContinue
}
