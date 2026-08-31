@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SIGN_CERT=%~dp0installer\GirtCodeSigning.pfx"
set "RESOLVE_PW=%~dp0installer\resolve-pfx-password.ps1"

echo === Building Girt (Release) ===
dotnet build src\Girt\Girt.csproj -c Release
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

if not exist "%SIGN_CERT%" (
    echo.
    echo ----------------------------------------------------------------------
    echo  Certificate file not found:
    echo    %SIGN_CERT%
    echo ----------------------------------------------------------------------
    echo  Add your .pfx next to the installer script or update SIGN_CERT in this .bat.
    echo ----------------------------------------------------------------------
    exit /b 1
)

call :ResolveSignTool
if errorlevel 1 exit /b 1

set "PW_TMP="
if "!ALLOW_EMPTY_PFX_PASSWORD!"=="1" (
    echo Using PFX without password ^(ALLOW_EMPTY_PFX_PASSWORD=1^).
) else (
    set "PW_TMP=%TEMP%\girt-pfx-password.txt"
    powershell -NoProfile -ExecutionPolicy Bypass -File "!RESOLVE_PW!" -OutFile "!PW_TMP!" 2>"!PW_TMP!.err"
    if errorlevel 1 (
        type "!PW_TMP!.err" 2>nul
        del "!PW_TMP!" 2>nul
        del "!PW_TMP!.err" 2>nul
        exit /b 1
    )
    del "!PW_TMP!.err" 2>nul
)

echo.
echo === Signing Girt.exe ===
if defined PW_TMP (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\sign-pe.ps1" -SignTool "%SIGNTOOL_EXE%" -Cert "%SIGN_CERT%" -Target "%~dp0src\Girt\bin\Release\net8.0-windows\Girt.exe" -PasswordFile "!PW_TMP!"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\sign-pe.ps1" -SignTool "%SIGNTOOL_EXE%" -Cert "%SIGN_CERT%" -Target "%~dp0src\Girt\bin\Release\net8.0-windows\Girt.exe"
)
if errorlevel 1 (
    echo.
    echo SIGNING FAILED - check PFX path, password ^(env or installer\code-signing-password.txt^), and signtool.
    if defined PW_TMP del "!PW_TMP!" 2>nul
    exit /b 1
)

call :ResolveIscc
if errorlevel 1 (
    if defined PW_TMP del "!PW_TMP!" 2>nul
    exit /b 1
)

echo.
echo === Compiling Installer ===
if defined PW_TMP (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\run-iscc-with-sign.ps1" -IsccExe "%ISCC_EXE%" -IssPath "%~dp0installer\Girt.iss" -SignToolExe "%SIGNTOOL_EXE%" -SignCert "%SIGN_CERT%" -PasswordFile "!PW_TMP!"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\run-iscc-with-sign.ps1" -IsccExe "%ISCC_EXE%" -IssPath "%~dp0installer\Girt.iss" -SignToolExe "%SIGNTOOL_EXE%" -SignCert "%SIGN_CERT%"
)
if errorlevel 1 (
    echo.
    echo INSTALLER COMPILE FAILED
    echo  If Inno reported a SignTool error, confirm signtool and PFX password.
    if defined PW_TMP del "!PW_TMP!" 2>nul
    exit /b 1
)

if defined PW_TMP del "!PW_TMP!" 2>nul

echo.
echo === Done ===
echo Output: installer\Output\GirtSetup.exe
goto :EOF

rem ---------------------------------------------------------------------------
rem  Scan one Windows Kits\bin root for the newest 10.* kit with signtool.
rem ---------------------------------------------------------------------------
:TryFindSignToolInKits
set "KITS_BIN=%~1"
if not exist "%KITS_BIN%" exit /b 0
for /f "delims=" %%v in ('dir /b /ad /o-n "%KITS_BIN%" 2^>nul ^| findstr /r /c:"^10\."') do (
    if exist "%KITS_BIN%\%%v\x64\signtool.exe" (
        set "SIGNTOOL_EXE=%KITS_BIN%\%%v\x64\signtool.exe"
        exit /b 0
    )
    if exist "%KITS_BIN%\%%v\arm64\signtool.exe" (
        set "SIGNTOOL_EXE=%KITS_BIN%\%%v\arm64\signtool.exe"
        exit /b 0
    )
)
exit /b 0

rem ---------------------------------------------------------------------------
rem  Locate Microsoft signtool.exe (required - signing is not skipped).
rem ---------------------------------------------------------------------------
:ResolveSignTool
set "SIGNTOOL_EXE="

if defined SIGNTOOL_PATH if exist "!SIGNTOOL_PATH!" (
    set "SIGNTOOL_EXE=!SIGNTOOL_PATH!"
    goto :SignToolFound
)
if exist "C:\buildtools\signtool.exe" (
    set "SIGNTOOL_EXE=C:\buildtools\signtool.exe"
    goto :SignToolFound
)

where signtool >nul 2>&1
if not errorlevel 1 (
    for /f "delims=" %%i in ('where signtool 2^>nul') do (
        set "SIGNTOOL_EXE=%%i"
        goto :SignToolFound
    )
)

call :TryFindSignToolInKits "%ProgramFiles(x86)%\Windows Kits\10\bin"
if defined SIGNTOOL_EXE goto :SignToolFound
call :TryFindSignToolInKits "%ProgramFiles%\Windows Kits\10\bin"

call :PrintSignToolHelp
exit /b 1

:SignToolFound
echo Using signtool: %SIGNTOOL_EXE%
exit /b 0

:PrintSignToolHelp
echo.
echo ======================================================================
echo   SIGNTOOL.EXE NOT FOUND - signing is required and is not skipped.
echo ======================================================================
echo.
echo   Install the Windows SDK (includes SignTool^):
echo     https://developer.microsoft.com/windows/downloads/windows-sdk/
echo.
echo   Or set SIGNTOOL_PATH to your signtool.exe ^(e.g. C:\buildtools\signtool.exe^).
echo.
echo   In Visual Studio Installer you can add the workload:
echo     "Desktop development with C++"  OR  individual "Windows SDK" / signing tools.
echo.
echo   After install, either:
echo     - Add the SDK "bin\x64" folder to your PATH, or
echo     - Run this script from "x64 Native Tools Command Prompt for VS", or
echo     - Rerun this .bat - it searches:
echo         %%ProgramFiles(x86)%%\Windows Kits\10\bin\^<version^>\x64\signtool.exe
echo.
echo   Verify manually:
echo     where signtool
echo ======================================================================
exit /b 1

rem ---------------------------------------------------------------------------
rem  Locate Inno Setup compiler (ISCC.exe).
rem ---------------------------------------------------------------------------
:ResolveIscc
set "ISCC_EXE="

where iscc >nul 2>&1
if not errorlevel 1 (
    for /f "delims=" %%i in ('where iscc 2^>nul') do (
        set "ISCC_EXE=%%i"
        goto :IsccFound
    )
)

if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" (
    set "ISCC_EXE=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
    goto :IsccFound
)
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" (
    set "ISCC_EXE=%ProgramFiles%\Inno Setup 6\ISCC.exe"
    goto :IsccFound
)

call :PrintInnoHelp
exit /b 1

:IsccFound
echo Using Inno Setup compiler: %ISCC_EXE%
exit /b 0

:PrintInnoHelp
echo.
echo ======================================================================
echo   INNO SETUP COMPILER (ISCC.EXE) NOT FOUND
echo ======================================================================
echo.
echo   Download and install Inno Setup 6 (free):
echo     https://jrsoftware.org/isinfo.php
echo.
echo   This script looks for:
echo     "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
echo     "%ProgramFiles%\Inno Setup 6\ISCC.exe"
echo     ... or ISCC.exe on your PATH.
echo.
echo   After install, run this .bat again.
echo ======================================================================
exit /b 1
