@echo off
rem Gera JATools_Diagnostico.txt na Area de Trabalho
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Diagnostico.ps1"
