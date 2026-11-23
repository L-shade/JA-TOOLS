<#
  Pacote do escritório: gera o instalador do JA para Revit (JA Famílias + JA Tools) numa pasta
  dist\Pacote_JA_<data>\  com:
    - JA_Setup_<versao>.exe
    - versoes.txt         (lido pelos plugins para avisar de versão nova)
    - Instalar tudo.bat   (instala e grava esta pasta como pasta de atualizações)
  Copie a pasta para a rede / OneDrive do escritório; cada computador roda "Instalar tudo.bat" uma vez.
  Uso:  GerarPacoteEscritorio.bat
#>
$ErrorActionPreference = "Stop"
function Fail($msg) { Write-Host ""; Write-Host "ERRO: $msg" -ForegroundColor Red; exit 1 }
$root = Split-Path $PSScriptRoot -Parent

[xml]$p = Get-Content (Join-Path $root "Directory.Build.props")
$v = ($p.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $v) { $v = "1.0.0" }

$env:CI = "1"      # the installer script does not open Explorer
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build\make-installer.ps1")
if ($LASTEXITCODE) { Fail "O instalador não foi gerado." }
$setup = Join-Path $root "dist\Instalador\JA_Setup_$v.exe"
if (-not (Test-Path $setup)) { Fail "Não achei $setup" }

$pac = Join-Path $root ("dist\Pacote_JA_" + (Get-Date -Format "yyyy-MM-dd"))
if (Test-Path $pac) { Remove-Item $pac -Recurse -Force }
New-Item -ItemType Directory $pac | Out-Null
Copy-Item $setup $pac
$utf8 = New-Object System.Text.UTF8Encoding($false)
# "JA=" for 2.0+; the two old keys so a computer still on the separate plugins is told too
[IO.File]::WriteAllText((Join-Path $pac "versoes.txt"), "JA=$v`r`nJA Tools=$v`r`nJA Famílias=$v`r`n", $utf8)

$bat = @"
@echo off
rem Instala o JA para Revit (JA Famílias + JA Tools) e grava esta pasta como a pasta de
rem atualizações (o Revit avisa ao abrir quando houver versão nova aqui).
chcp 65001 >nul
cd /d "%~dp0"
tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe" >nul && (
  echo Feche o Revit antes de instalar.
  pause
  exit /b 1
)
echo Instalando o JA para Revit $v...
start /wait "" "%~dp0JA_Setup_$v.exe" /SILENT /SUPPRESSMSGBOXES /NORESTART
if not exist "%APPDATA%\JA Arquitetos" mkdir "%APPDATA%\JA Arquitetos"
set "P=%~dp0"
if "%P:~-1%"=="\" set "P=%P:~0,-1%"
> "%APPDATA%\JA Arquitetos\pasta-atualizacoes.txt" echo %P%
echo.
echo Pronto: JA para Revit $v instalado (abas JA Famílias e JA Tools).
echo Pasta de atualizações: %P%
pause
"@
[IO.File]::WriteAllText((Join-Path $pac "Instalar tudo.bat"), $bat, $utf8)
$leia = "Pacote JA ($((Get-Date).ToString('dd/MM/yyyy')))`r`n`r`n1. Copie esta pasta para a rede ou OneDrive do escritório.`r`n2. Em cada computador, feche o Revit e dê dois cliques em ""Instalar tudo.bat"".`r`n3. Nas próximas versões, gere um pacote novo e substitua os arquivos desta pasta: ao abrir o Revit, os plugins avisam que há versão nova.`r`n"
[IO.File]::WriteAllText((Join-Path $pac "LEIA-ME.txt"), $leia, $utf8)

Write-Host ""
Write-Host "Pronto!" -ForegroundColor Green
Write-Host "  Pacote: $pac"
Write-Host "  JA para Revit $v"
Start-Process explorer.exe $pac
