@echo off
REM Deployment script for Going Medieval LLM NPCs mod
REM Game location: E:\SteamLibrary\steamapps\common\Going Medieval

echo ==========================================
echo Going Medieval LLM NPCs - Deploy Script
echo ==========================================

set GAME_DIR=E:\SteamLibrary\steamapps\common\Going Medieval
set MOD_DIR=%~dp0
set BUILD_DIR=%MOD_DIR%bin\Release\net472

echo.
echo Game Directory: %GAME_DIR%
echo Mod Directory: %MOD_DIR%
echo.

REM Check if build exists
if not exist "%BUILD_DIR%\LLM_NPCs.dll" (
    echo [ERROR] Build not found!
    echo Building now...
    cd /d "%MOD_DIR%"
    dotnet build --configuration Release
    
    if errorlevel 1 (
        echo [ERROR] Build failed!
        pause
        exit /b 1
    )
)

echo [1/4] Copying mod DLL...
copy /Y "%BUILD_DIR%\LLM_NPCs.dll" "%GAME_DIR%\BepInEx\plugins\"
if errorlevel 1 (
    echo [ERROR] Failed to copy DLL. Is BepInEx installed?
    echo.
    echo To install BepInEx:
    echo 1. Download from https://github.com/BepInEx/BepInEx/releases
    echo 2. Extract to: %GAME_DIR%
    echo 3. Run game once to generate folders
    echo 4. Run this script again
    pause
    exit /b 1
)

echo [2/4] Copying JSON dependency...
if exist "%BUILD_DIR%\Newtonsoft.Json.dll" (
    copy /Y "%BUILD_DIR%\Newtonsoft.Json.dll" "%GAME_DIR%\BepInEx\plugins\"
) else (
    echo [WARNING] Newtonsoft.Json.dll not found in build output.
)

echo [3/4] Removing obsolete SQLite plugin dependencies...
if exist "%GAME_DIR%\BepInEx\plugins\System.Data.SQLite.dll" del /Q "%GAME_DIR%\BepInEx\plugins\System.Data.SQLite.dll"
if exist "%GAME_DIR%\BepInEx\plugins\x64\SQLite.Interop.dll" del /Q "%GAME_DIR%\BepInEx\plugins\x64\SQLite.Interop.dll"
if exist "%GAME_DIR%\BepInEx\plugins\x86\SQLite.Interop.dll" del /Q "%GAME_DIR%\BepInEx\plugins\x86\SQLite.Interop.dll"

echo [4/4] Verifying installation...
if exist "%GAME_DIR%\BepInEx\plugins\LLM_NPCs.dll" (
    echo.
    echo ==========================================
    echo [SUCCESS] Mod installed!
    echo ==========================================
    echo.
    echo Next steps:
    echo 1. Start the dashboard: python dashboard\dashboard_server.py
    echo 2. Verify http://127.0.0.1:8714/health shows Player2 online.
    echo 3. Start Going Medieval and load a save.
    echo 4. Open http://127.0.0.1:8714/dashboard to watch memory, pressures, incidents, and the game stream.
    echo.
    echo ALTERNATIVE: Edit config directly:
    echo %GAME_DIR%\BepInEx\config\com.goingmedieval.llm_npcs.cfg
    echo.
    echo IN-GAME CONTROLS:
    echo - Press Return while playing to talk to settlers.
    echo - Press BackQuote to open the Social Hub.
    echo.
    echo Log file: %GAME_DIR%\BepInEx\logs\LogOutput.log
    echo Config file: %GAME_DIR%\BepInEx\config\com.goingmedieval.llm_npcs.cfg
    echo.
    exit /b 0
) else (
    echo [ERROR] Installation failed!
    pause
    exit /b 1
)
