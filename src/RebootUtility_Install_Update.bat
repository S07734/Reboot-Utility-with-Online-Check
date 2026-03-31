@echo off
setlocal EnableDelayedExpansion

:: ============================================================
:: RebootUtility Install / Update Script
:: ============================================================

:: FIX: Quote the path in Start-Process so spaces in folder names don't break elevation
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrative privileges...
    powershell -Command "Start-Process -FilePath cmd.exe -ArgumentList '/c \"%~dpnx0\"' -Verb RunAs"
    exit /b
)

:: ------------------------------------------------------------
:: Kill running instance (single call with /F /T is sufficient)
:: FIX: Removed misleading "retry" that fired even when process wasn't running
:: ------------------------------------------------------------
taskkill /IM rebootutility.exe /F /T >nul 2>&1

:: Kill any leftover tmp*.tmp.exe update processes
for /f "tokens=*" %%i in ('dir /b "%TEMP%\tmp*.tmp.exe" 2^>nul') do (
    taskkill /IM "%%i" /F /T >nul 2>&1
)

:: Wait for processes to fully release file locks
timeout /t 2 /nobreak >nul

:: ------------------------------------------------------------
:: Delete leftover temp update files
:: FIX: Single cleanup pass is sufficient after the 2-second wait above
:: ------------------------------------------------------------
for /f "tokens=*" %%i in ('dir /b "%TEMP%\tmp*.tmp.exe" 2^>nul') do (
    del /q /f "%TEMP%\%%i" >nul 2>&1
)
for /f "tokens=*" %%i in ('dir /b "%TEMP%\tmp*.tmp" 2^>nul') do (
    del /q /f "%TEMP%\%%i" >nul 2>&1
)

:: ------------------------------------------------------------
:: Clean up registry Run entries that point to temp files
:: FIX: Previous cmd-only parsing was broken — value name extraction was wrong
::      and tokens=3 silently dropped paths containing spaces.
::      Using PowerShell for reliable registry enumeration.
:: ------------------------------------------------------------
powershell -Command ^
    "$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run';" ^
    "$props = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue;" ^
    "if ($props) {" ^
    "    $props.PSObject.Properties | Where-Object { $_.Value -like '*\tmp*.tmp.exe*' } | ForEach-Object {" ^
    "        Write-Host ('Deleting startup entry: ' + $_.Name + ' -> ' + $_.Value);" ^
    "        Remove-ItemProperty -Path $key -Name $_.Name -Force;" ^
    "    }" ^
    "}"

:: ------------------------------------------------------------
:: Download new executable
:: ------------------------------------------------------------
set "destFolder=%SystemDrive%\rebootutil"
set "destFile=%destFolder%\RebootUtility.exe"
set "url=https://raw.githubusercontent.com/S07734/Reboot-Utility-with-Online-Check/main/bin/latest/RebootUtility.exe"

if not exist "%destFolder%" mkdir "%destFolder%"

:: FIX: Delete any existing/partial file before downloading so a failed
::      download can't leave a corrupt exe that passes the existence check
if exist "%destFile%" del /q /f "%destFile%" >nul 2>&1

set "updaterUrl=https://raw.githubusercontent.com/S07734/Reboot-Utility-with-Online-Check/main/bin/latest/RebootUtilityUpdate.exe"
set "updaterFile=%destFolder%\RebootUtilityUpdate.exe"

echo Downloading RebootUtility...
powershell -Command "Invoke-WebRequest -Uri '%url%' -OutFile '%destFile%' -ErrorAction Stop"

if not exist "%destFile%" (
    echo ERROR: Download failed. RebootUtility.exe was not created.
    pause
    exit /b 1
)

echo Downloading RebootUtilityUpdate...
if exist "%updaterFile%" del /q /f "%updaterFile%" >nul 2>&1
powershell -Command "Invoke-WebRequest -Uri '%updaterUrl%' -OutFile '%updaterFile%' -ErrorAction Stop"

:: ------------------------------------------------------------
:: Register startup entry and launch
:: ------------------------------------------------------------
set "runKey=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
reg add "%runKey%" /v "Reboot Utility" /t REG_SZ /d "\"%destFile%\"" /f >nul 2>&1

echo Done. Launching RebootUtility...
start "" "%destFile%"

exit /b 0
