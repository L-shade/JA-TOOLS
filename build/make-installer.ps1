<#
  Gera a pasta do instalador do JA para Revit (abas JA Famílias e JA Tools):  dist\Instalador\
     JA_Setup_<versao>.exe   (instala os dois add-ins em qualquer computador com Revit 2024-2027)
     LEIA-ME.txt
  e o arquivo  dist\JA_Instalador_<versao>.zip  com essa pasta, pronto para enviar.

  1. Confere os pre-requisitos da maquina de COMPILACAO (.NET SDK e Inno Setup) e oferece
     instalar o que faltar com o winget.
  2. Compila os dois add-ins para todas as versoes do Revit e o motor de uniao para todas as familias
     do AutoCAD (Revit/AutoCAD instalados nao sao necessarios: as APIs vem do NuGet).
  3. Gera o Setup.exe com o Inno Setup.

  Uso:  duplo clique em GerarInstalador.bat
        .\build\make-installer.ps1 -RevitVersions 2025,2026
#>
param(
  [string[]]$RevitVersions = @("2024", "2025", "2026", "2027"),
  [string]$Configuration   = "Release",
  [switch]$PreferNuGet,
  [switch]$Yes,
  [switch]$Strict
)
$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$root = Split-Path $PSScriptRoot -Parent

function Fail($msg) { Write-Host ""; Write-Host "ERRO: $msg" -ForegroundColor Red; exit 1 }

function Ask($question) {
  if ($Yes) { return $true }
  $a = Read-Host "$question [S/n]"
  return ($a -eq "" -or $a -match '^[sSyY]')
}

function Install-WithWinget([string]$id, [string]$label) {
  if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    Fail "$label nao encontrado e o winget nao esta disponivel. Instale manualmente: $id"
  }
  Write-Host "Instalando $label (winget install $id)..." -ForegroundColor Cyan
  winget install --id $id -e --accept-package-agreements --accept-source-agreements | Out-Host
  # PATH novo sem reiniciar o terminal
  $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
}

# ---------------------------------------------------------------- .NET SDK
# O SDK 10 compila net48, net8 e net10 (Revit/AutoCAD 2027). Com o SDK 8 sai tudo menos o 2027.
$sdks = @()
if (Get-Command dotnet -ErrorAction SilentlyContinue) { $sdks = & dotnet --list-sdks }
$hasSdk10 = [bool]($sdks | Where-Object { $_ -match '^(1\d)\.' })
$hasSdk8  = [bool]($sdks | Where-Object { $_ -match '^([89]|1\d)\.' })
if (-not $hasSdk10) {
  $why = if ($hasSdk8) { "O .NET 10 SDK permite incluir o Revit/AutoCAD 2027." } else { "O .NET SDK e necessario para compilar." }
  if (Ask "$why Instalar o .NET 10 SDK agora?") { Install-WithWinget "Microsoft.DotNet.SDK.10" ".NET 10 SDK" }
  $sdks = & dotnet --list-sdks
  $hasSdk8 = [bool]($sdks | Where-Object { $_ -match '^([89]|1\d)\.' })
}
if (-not $hasSdk8) { Fail ".NET SDK 8 ou mais novo nao encontrado (winget install Microsoft.DotNet.SDK.10)." }

# ---------------------------------------------------------------- Inno Setup
function Find-Iscc {
  $p = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
  ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
  if (-not $p) { $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue; if ($cmd) { $p = $cmd.Source } }
  return $p
}
$iscc = Find-Iscc
if (-not $iscc) {
  if (Ask "Inno Setup 6 (gera o Setup.exe) nao encontrado. Instalar agora?") { Install-WithWinget "JRSoftware.InnoSetup" "Inno Setup 6" }
  $iscc = Find-Iscc
}
if (-not $iscc) { Fail "Inno Setup 6 nao encontrado (winget install JRSoftware.InnoSetup)." }

# ---------------------------------------------------------------- versao
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = "1.0.0" }

# ---------------------------------------------------------------- compilacao
$dist = Join-Path $root "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
$buildArgs = @{ RevitVersions = $RevitVersions; Configuration = $Configuration }
if ($PreferNuGet) { $buildArgs.PreferNuGet = $true }
if ($Strict) { $buildArgs.Strict = $true }
& (Join-Path $PSScriptRoot "build.ps1") @buildArgs

$built = Get-ChildItem $dist -Directory -Filter "Revit*" -ErrorAction SilentlyContinue
if (-not $built) { Fail "A compilacao nao gerou nada em dist\." }
foreach ($b in $built) {
  if (-not (Test-Path "$($b.FullName)\JA\MergeEngine")) {
    Write-Warning "$($b.Name): sem motor de uniao (o DWG unico nao funcionara; o resto funciona)."
  }
}

# ---------------------------------------------------------------- instalador
Write-Host "Gerando o instalador com $iscc" -ForegroundColor Cyan
& $iscc "/Qp" "/DAppVersion=$version" (Join-Path $root "installer\JA.iss")
if ($LASTEXITCODE) { Fail "ISCC falhou (codigo $LASTEXITCODE)." }

$folder = Join-Path $dist "Instalador"
Copy-Item (Join-Path $root "installer\LEIA-ME.txt") $folder -Force
$zip = Join-Path $dist "JA_Instalador_$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$folder\*" -DestinationPath $zip

Write-Host ""
Write-Host "Pronto!" -ForegroundColor Green
Write-Host "  Pasta do instalador: $folder"
Write-Host "  Setup:               $(Join-Path $folder "JA_Setup_$version.exe")"
Write-Host "  Para enviar:         $zip"
Write-Host ("  Revit incluidos:     " + (($built | ForEach-Object { $_.Name -replace 'Revit', '' }) -join ", "))
if (-not $env:CI) { Start-Process explorer.exe $folder }
