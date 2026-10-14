@echo off
rem vHA setup launcher. VhaSetup.exe asks for administrator rights by itself (UAC).
setlocal
set "SETUP=%~dp0setup\VhaSetup.exe"
if not exist "%SETUP%" (echo VhaSetup.exe non trovato in "%~dp0setup" & exit /b 2)
if not "%~1"=="" ( "%SETUP%" %* & exit /b %ERRORLEVEL% )

:menu
echo.
echo  vHA - installazione guidata
echo  --------------------------------
echo   1. Crea o modifica il file di configurazione (wizard)
echo   2. Mostra cosa verra' fatto su questo nodo (piano)
echo   3. Installa su questo nodo
echo   4. Crea cluster e ruoli (da eseguire dopo aver installato tutti i nodi)
echo   5. Verifica
echo   0. Esci
set /p CHOICE=Scelta: 
if "%CHOICE%"=="1" "%SETUP%" wizard --config "%~dp0vha-deployment.json" & goto menu
if "%CHOICE%"=="2" "%SETUP%" plan --config "%~dp0vha-deployment.json" & goto menu
if "%CHOICE%"=="3" "%SETUP%" install --config "%~dp0vha-deployment.json" & goto menu
if "%CHOICE%"=="4" "%SETUP%" cluster --config "%~dp0vha-deployment.json" & goto menu
if "%CHOICE%"=="5" "%SETUP%" check --config "%~dp0vha-deployment.json" & goto menu
if "%CHOICE%"=="0" exit /b 0
goto menu
