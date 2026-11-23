@echo off
rem ==========================================================================
rem  Duplo clique: compila o JA para Revit (abas JA Famílias e JA Tools) e gera
rem  a pasta  dist\Instalador\  com o JA_Setup_<versao>.exe — um instalador só
rem  para os dois. Revit e AutoCAD NAO precisam estar instalados nesta maquina.
rem  O que faltar (.NET SDK, Inno Setup) e oferecido para instalar com o winget.
rem ==========================================================================
cd /d "%~dp0"
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\make-installer.ps1" %*
echo.
pause
