@echo off
rem Compila o JA para Revit (JA Famílias + JA Tools) para todas as versoes do Revit, sem gerar
rem instalador, e grava o resultado em build-log.txt, nesta pasta.
cd /d "%~dp0"
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0build\build.ps1' *>&1 | Tee-Object -FilePath '%~dp0build-log.txt'"
echo.
echo Resultado gravado em build-log.txt
pause
