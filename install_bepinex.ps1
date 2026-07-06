# BepInEx 5.4.23.2 Installer for Going Medieval
# Run this script as Administrator

param(
    [string]$GamePath = "E:\SteamLibrary\steamapps\common\Going Medieval"
)

$ErrorActionPreference = "Stop"

Write-Host "=== BepInEx Installer for Going Medieval ===" -ForegroundColor Cyan
Write-Host ""

# Check if game path exists
if (-not (Test-Path $GamePath)) {
    Write-Error "Game not found at: $GamePath"
    Write-Host "Please update the GamePath parameter to your Going Medieval installation folder"
    exit 1
}

Write-Host "Game found at: $GamePath" -ForegroundColor Green

# Check if BepInEx already exists
$bepinexPath = Join-Path $GamePath "BepInEx"
if (Test-Path $bepinexPath) {
    Write-Host "BepInEx folder already exists. Removing old installation..." -ForegroundColor Yellow
    Remove-Item -Path $bepinexPath -Recurse -Force -ErrorAction SilentlyContinue
}

# Download URL for BepInEx 5.4.23.2 (x64 version for Unity games)
$bepinexUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_x64_5.4.23.2.zip"
$tempZip = "$env:TEMP\BepInEx_x64_5.4.23.2.zip"

Write-Host "Downloading BepInEx 5.4.23.2..." -ForegroundColor Cyan
Write-Host "URL: $bepinexUrl"

try {
    # Download BepInEx
    Invoke-WebRequest -Uri $bepinexUrl -OutFile $tempZip -UseBasicParsing
    Write-Host "Download complete!" -ForegroundColor Green
}
catch {
    Write-Error "Failed to download BepInEx. Error: $_"
    Write-Host "Please manually download from: https://github.com/BepInEx/BepInEx/releases"
    exit 1
}

# Extract BepInEx
Write-Host "Extracting BepInEx to game folder..." -ForegroundColor Cyan
Expand-Archive -Path $tempZip -DestinationPath $GamePath -Force

# Clean up
Remove-Item -Path $tempZip -Force

Write-Host "" -ForegroundColor Green
Write-Host "=== BepInEx Installation Complete! ===" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Launch Going Medieval from Steam" -ForegroundColor White
Write-Host "2. A BepInEx console window should appear alongside the game" -ForegroundColor White
Write-Host "3. Close the game after it loads (to generate config files)" -ForegroundColor White
Write-Host "4. Copy your mod files to: $GamePath\BepInEx\plugins\" -ForegroundColor White
Write-Host ""
Write-Host "Press any key to exit..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
