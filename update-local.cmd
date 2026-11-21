@echo off
chcp 65001 >nul
rem Собрать ROSHIQ и тихо установить поверх (программа закроется и запустится снова).
cd /d "%~dp0"
set LOG=%~dp0update-local.log
echo START %date% %time% > "%LOG%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" > nul 2>&1
echo BUILD EXIT %errorlevel% >> "%LOG%"
if errorlevel 1 goto :fail
set VER=
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content '%~dp0Directory.Build.props' -Raw)).Project.PropertyGroup.Version | Select-Object -First 1"`) do set VER=%%v
set SETUP=%~dp0dist\Roshiq-Setup-%VER%.exe
if not exist "%SETUP%" goto :fail
echo INSTALL %SETUP% >> "%LOG%"
"%SETUP%" /VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART /LOG="%~dp0install.log"
echo INSTALL EXIT %errorlevel% >> "%LOG%"
if errorlevel 1 goto :fail
echo DONE %date% %time% >> "%LOG%"
exit /b 0
:fail
echo FAILED %date% %time% >> "%LOG%"
exit /b 1
