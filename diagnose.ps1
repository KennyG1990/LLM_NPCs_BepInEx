# LLM NPCs Diagnostic Script
# Run this to check mod health

param(
    [string]$GameDir = "E:\SteamLibrary\steamapps\common\Going Medieval"
)

function Write-Header($text) {
    Write-Host "`n==========================================" -ForegroundColor Cyan
    Write-Host $text -ForegroundColor Cyan
    Write-Host "==========================================" -ForegroundColor Cyan
}

function Write-OK($text) { Write-Host "[OK] $text" -ForegroundColor Green }
function Write-Warn($text) { Write-Host "[!] $text" -ForegroundColor Yellow }
function Write-Err($text) { Write-Host "[X] $text" -ForegroundColor Red }

Write-Header "LLM NPCs Diagnostics"

# Check 1: Game Directory
if (Test-Path $GameDir) {
    Write-OK "Game directory exists"
}
else {
    Write-Err "Game directory not found: $GameDir"
    exit 1
}

# Check 2: BepInEx
$bepinexCore = "$GameDir\BepInEx\core\BepInEx.dll"
if (Test-Path $bepinexCore) {
    Write-OK "BepInEx installed"
    $version = (Get-Item $bepinexCore).VersionInfo.FileVersion
    Write-Host "     Version: $version"
}
else {
    Write-Err "BepInEx NOT installed"
    Write-Host "     Download from: https://github.com/BepInEx/BepInEx/releases"
}

# Check 3: Mod DLL
$modDll = "$GameDir\BepInEx\plugins\LLM_NPCs.dll"
if (Test-Path $modDll) {
    Write-OK "Mod DLL found"
    $version = (Get-Item $modDll).VersionInfo.FileVersion
    $lastWrite = (Get-Item $modDll).LastWriteTime
    Write-Host "     Version: $version"
    Write-Host "     Last modified: $lastWrite"
}
else {
    Write-Err "Mod DLL NOT found in plugins folder"
}

# Check 4: SQLite
$sqliteInterop = "$GameDir\BepInEx\plugins\x64\SQLite.Interop.dll"
if (Test-Path $sqliteInterop) {
    Write-OK "SQLite native library found"
}
else {
    Write-Err "SQLite.Interop.dll NOT found"
    Write-Host "     Expected: $GameDir\BepInEx\plugins\x64\SQLite.Interop.dll"
    Write-Host "     Fix: Copy from NuGet package or build output"
}

# Check 5: Log File
$logFile = "$GameDir\BepInEx\logs\LogOutput.log"
if (Test-Path $logFile) {
    Write-OK "Log file exists"
    
    # Check for mod loading
    $modLoaded = Select-String -Path $logFile -Pattern "LLM NPCs.*loaded successfully" -Quiet
    if ($modLoaded) {
        Write-OK "Mod loaded successfully (found in log)"
    }
    else {
        Write-Warn "Mod load status unclear - check log manually"
    }
    
    # Check for errors
    $errors = Select-String -Path $logFile -Pattern "\[Error.*LLM NPCs|\[Fatal.*LLM NPCs" | Select-Object -Last 5
    if ($errors) {
        Write-Err "Recent errors found:"
        $errors | ForEach-Object { Write-Host "     $_" -ForegroundColor Red }
    }
    else {
        Write-OK "No errors found in log"
    }
    
    # Check for decisions
    $decisions = Select-String -Path $logFile -Pattern "\[.*\] (rest|eat|flee|defend|continue_job|switch_job)" | Select-Object -Last 3
    if ($decisions) {
        Write-OK "Decision entries found in log:"
        $decisions | ForEach-Object { Write-Host "     $_" -ForegroundColor Gray }
    }
    else {
        Write-Warn "No decision entries found (may be normal if game not run yet)"
    }
}
else {
    Write-Warn "Log file not found - has game been run?"
}

# Check 6: Config
$configFile = "$GameDir\BepInEx\config\com.goingmedieval.llm_npcs.cfg"
if (Test-Path $configFile) {
    Write-OK "Config file exists"
    
    $content = Get-Content $configFile -Raw
    
    # Check API key
    if ($content -match "ApiKey\s*=\s*(sk-or-v1-[a-zA-Z0-9]+)") {
        $key = $matches[1]
        $masked = $key.Substring(0, 10) + "..." + $key.Substring($key.Length - 4)
        Write-OK "API key configured: $masked"
    }
    elseif ($content -match "ApiKey\s*=\s*\w+") {
        Write-Warn "API key appears invalid format"
    }
    else {
        Write-Err "API key NOT configured"
        Write-Host "     Get free key at: https://openrouter.ai/keys"
    }
    
    # Check if enabled
    if ($content -match "EnableMod\s*=\s*true") {
        Write-OK "Mod is enabled"
    }
    elseif ($content -match "EnableMod\s*=\s*false") {
        Write-Warn "Mod is DISABLED in config"
    }
}
else {
    Write-Warn "Config file not found - will be created on first run"
}

# Check 7: Memory Database
$dbPath = "$env:APPDATA\Going Medieval\LLM_NPCs\memory\npc_memory.db"
if (Test-Path $dbPath) {
    $size = (Get-Item $dbPath).Length / 1KB
    Write-OK "Memory database exists (${size:N1} KB)"
    
    # Try to check table count
    try {
        $tables = sqlite3 $dbPath ".tables" 2>$null
        if ($tables) {
            Write-OK "Database is accessible"
        }
    }
    catch {
        Write-Warn "Cannot verify database contents (sqlite3 not in PATH)"
    }
}
else {
    Write-Warn "Memory database not created yet (will be created on first run)"
}

# Check 8: .NET SDK
Write-Host "`n--- Build Environment ---"
try {
    $dotnetVersion = dotnet --version 2>$null
    if ($dotnetVersion) {
        Write-OK ".NET SDK found: $dotnetVersion"
    }
    else {
        Write-Err ".NET SDK not found - required to build mod"
    }
}
catch {
    Write-Err ".NET SDK not found - required to build mod"
    Write-Host "     Download from: https://dotnet.microsoft.com/download"
}

# Summary
Write-Header "Summary"
Write-Host "Review the checks above. If all show [OK], the mod should work."
Write-Host "If there are [X] errors, check DEBUGGING.md for solutions."
Write-Host ""
Write-Host "Quick actions:"
Write-Host "  - View full log: notepad '$logFile'"
Write-Host "  - Edit config: notepad '$configFile'"
Write-Host "  - Check database: '$dbPath'"
Write-Host ""