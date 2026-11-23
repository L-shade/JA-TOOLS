@echo off
rem ==========================================================================
rem  Duplo clique: gera o instalador do JA para Revit e o pacote do escritório
rem  em  dist\Pacote_JA_<data>\  com "Instalar tudo.bat" e versoes.txt.
rem ==========================================================================
cd /d "%~dp0"
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\make-package.ps1" %*
echo.
pause
