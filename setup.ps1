# Going Medieval LLM NPCs - Automated Setup Script
# Run this in PowerShell as Administrator
# This script automates: BepInEx download, DLL extraction, building, and deployment

param(
    [string]$GameDir = "E:\SteamLibrary\steamapps\common\Going Medieval",
    [string]$ModDir = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

function Write-Header($text) {
    Write-Host "`n==========================================" -ForegroundColor Cyan
    Write-Host $text -ForegroundColor Cyan
    Write-Host "==========================================" -ForegroundColor Cyan
}

function Write-Success($text) {
    Write-Host "[OK] $text" -ForegroundColor Green
}

function Write-Warning($text) {
    Write-Host "[!] $text" -ForegroundColor Yellow
}

function Write-Error($text) {
    Write-Host "[X] $text" -ForegroundColor Red
}

# Check if running as admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")
if (-not $isAdmin) {
    Write-Warning "Not running as Administrator. Some operations may fail."
}

Write-Header "Going Medieval LLM NPCs - Setup"
Write-Host "Game Directory: $GameDir"
Write-Host "Mod Directory: $ModDir"

# Step 1: Check Game Installation
Write-Header "Step 1: Verifying Game Installation"
if (-not (Test-Path $GameDir)) {
    Write-Error "Game not found at: $GameDir"
    Write-Host "Please update the `$GameDir variable in this script"
    exit 1
}
Write-Success "Game found"

# Step 2: Download and Install BepInEx
Write-Header "Step 2: Installing BepInEx"
$bepinexUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_x64_5.4.23.2.zip"
$bepinexZip = "$env:TEMP\BepInEx.zip"

if (Test-Path "$GameDir\BepInEx\core\BepInEx.dll") {
    Write-Success "BepInEx already installed"
}
else {
    Write-Host "Downloading BepInEx..."
    try {
        Invoke-WebRequest -Uri $bepinexUrl -OutFile $bepinexZip -UseBasicParsing
        Write-Success "Downloaded BepInEx"
        
        Write-Host "Extracting to game folder..."
        Expand-Archive -Path $bepinexZip -DestinationPath $GameDir -Force
        Write-Success "BepInEx installed"
        
        Remove-Item $bepinexZip -ErrorAction SilentlyContinue
    }
    catch {
        Write-Error "Failed to download/install BepInEx"
        Write-Host "Please manually download from: https://github.com/BepInEx/BepInEx/releases"
        exit 1
    }
}

# Step 3: Extract Game DLLs
Write-Header "Step 3: Extracting Game DLLs"
$managedDir = "$GameDir\Going Medieval_Data\Managed"
$libDir = "$ModDir\lib"

if (-not (Test-Path $managedDir)) {
    Write-Error "Game Managed folder not found: $managedDir"
    exit 1
}

New-Item -ItemType Directory -Force -Path $libDir | Out-Null

$requiredDlls = @(
    "Assembly-CSharp.dll",
    "Assembly-CSharp-firstpass.dll",
    "UnityEngine.dll",
    "UnityEngine.CoreModule.dll"
)

$allFound = $true
foreach ($dll in $requiredDlls) {
    $source = "$managedDir\$dll"
    $dest = "$libDir\$dll"
    
    if (Test-Path $source) {
        Copy-Item $source $dest -Force
        Write-Success "Copied $dll"
    }
    else {
        Write-Warning "Missing: $dll"
        $allFound = $false
    }
}

if (-not $allFound) {
    Write-Error "Some DLLs were not found. Game version may have changed."
}

# Step 4: Check .NET SDK
Write-Header "Step 4: Checking .NET SDK"
try {
    $dotnetVersion = dotnet --version
    Write-Success ".NET SDK found: $dotnetVersion"
}
catch {
    Write-Error ".NET SDK not found"
    Write-Host "Please install .NET 6 SDK or .NET Framework 4.7.2 SDK"
    Write-Host "Download from: https://dotnet.microsoft.com/download"
    exit 1
}

# Step 5: Build the Mod
Write-Header "Step 5: Building Mod"
Set-Location $ModDir

try {
    Write-Host "Restoring packages..."
    dotnet restore | Out-Null
    
    Write-Host "Building..."
    dotnet build --configuration Release
    
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed"
    }
    Write-Success "Build successful"
}
catch {
    Write-Error "Build failed"
    Write-Host $_.Exception.Message
    exit 1
}

# Step 6: Deploy
Write-Header "Step 6: Deploying to Game"
$pluginsDir = "$GameDir\BepInEx\plugins"
$buildDir = "$ModDir\bin\Release\net472"

New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null

# Copy main DLL
Copy-Item "$buildDir\LLM_NPCs.dll" $pluginsDir -Force
Write-Success "Copied LLM_NPCs.dll"

# Copy SQLite dependencies
if (Test-Path "$buildDir\System.Data.SQLite.dll") {
    Copy-Item "$buildDir\System.Data.SQLite.dll" $pluginsDir -Force
    Write-Success "Copied System.Data.SQLite.dll"
}

# Copy SQLite native library
$nativeDir = "$pluginsDir\x64"
New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null

$sqliteInterop = "$buildDir\x64\SQLite.Interop.dll"
if (-not (Test-Path $sqliteInterop)) {
    # Try to find in NuGet cache
    $nugetCache = "$env:USERPROFILE\.nuget\packages"
    $sqlitePackage = Get-ChildItem -Path $nugetCache -Recurse -Filter "SQLite.Interop.dll" | Select-Object -First 1
    if ($sqlitePackage) {
        $sqliteInterop = $sqlitePackage.FullName
    }
}

if (Test-Path $sqliteInterop) {
    Copy-Item $sqliteInterop $nativeDir -Force
    Write-Success "Copied SQLite.Interop.dll"
}
else {
    Write-Warning "SQLite.Interop.dll not found automatically"
    Write-Host "You may need to manually copy it from:"
    Write-Host "  %USERPROFILE%\.nuget\packages\stub.system.data.sqlite.core.netframework\1.0.118\runtimes\win-x64\native"
}

# Step 7: Create Config
Write-Header "Step 7: Configuration"
$configDir = "$GameDir\BepInEx\config"
$configFile = "$configDir\com.goingmedieval.llm_npcs.cfg"

New-Item -ItemType Directory -Force -Path $configDir | Out-Null

if (-not (Test-Path $configFile)) {
    @"
[General]
EnableMod = true

[LLM]
ApiKey = 
Model = anthropic/claude-3.5-sonnet
Temperature = 0.7

[Gameplay]
DecisionInterval = 10

[Debug]
LogDecisions = true
"@ | Out-File -FilePath $configFile -Encoding UTF8
    Write-Success "Created default config"
}

# Step 8: Summary
Write-Header "Setup Complete!"
Write-Host ""
Write-Success "Mod deployed to: $pluginsDir"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Edit config: $configFile"
Write-Host "2. Add your OpenRouter API key (get free at https://openrouter.ai/keys)"
Write-Host "3. Start Going Medieval"
Write-Host "4. Press F1 in-game to verify mod loads"
Write-Host "5. Check logs: $GameDir\BepInEx\logs\LogOutput.log"
Write-Host ""
Write-Host "To rebuild after code changes, run:"
Write-Host "  dotnet build --configuration Release" -ForegroundColor Cyan
Write-Host "  Copy-Item bin\Release\net472\LLM_NPCs.dll '$pluginsDir'" -ForegroundColor Cyan
Write-Host ""

$startGame = Read-Host "Start Going Medieval now? (Y/N)"
if ($startGame -eq 'Y' -or $startGame -eq 'y') {
    Start-Process "steam://rungameid/1161580"
}