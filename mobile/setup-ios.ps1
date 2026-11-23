$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$appDir = Join-Path $here 'LolRemoteApp'

Write-Host ''
Write-Host '=== LolRemote iOS app telepitese (Expo) ===' -ForegroundColor Cyan

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Host 'Nincs telepitve a Node.js. Toltsd le innen (LTS): https://nodejs.org' -ForegroundColor Red
    Read-Host 'Nyomj Entert a kilepeshez'
    exit 1
}

Set-Location $here
if (-not (Test-Path $appDir)) {
    Write-Host '1/4  Expo projekt letrehozasa...' -ForegroundColor Yellow
    npx --yes create-expo-app@latest LolRemoteApp --template blank
}

Set-Location $appDir
Write-Host '2/4  Csomagok telepitese (WebView, tarolo)...' -ForegroundColor Yellow
npx --yes expo install react-native-webview @react-native-async-storage/async-storage expo-keep-awake expo-camera

Write-Host '3/4  LolRemote fajlok bemasolasa...' -ForegroundColor Yellow
Copy-Item (Join-Path $here 'App.js') (Join-Path $appDir 'App.js') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $appDir 'assets') | Out-Null
Copy-Item (Join-Path $here 'icon.png') (Join-Path $appDir 'assets\lolremote-icon.png') -Force
node (Join-Path $here 'patch-app-json.js') $appDir

Write-Host '4/4  Expo inditasa. Az iPhone-on nyisd meg az Expo Go appot, es olvasd be a QR-kodot a Kamera appal.' -ForegroundColor Green
Write-Host '     Ha a telefon nem talalja (pl. iskolai WiFi), allitsd le (Ctrl+C) es inditsd igy: npx expo start --tunnel' -ForegroundColor DarkGray
npx expo start
