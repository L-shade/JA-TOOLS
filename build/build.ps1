<#
  JA para Revit: builds the add-in (one DLL, two ribbon tabs: JA Famílias and JA Tools) for every
  supported Revit version and the AutoCAD merge engine for every AutoCAD runtime family, then
  assembles ready-to-deploy folders under .\dist (one manifest JA.addin starting the two tabs).

    .\build\build.ps1                          # everything (local installs or NuGet)
    .\build\build.ps1 -RevitVersions 2025 -Deploy
    .\build\build.ps1 -PreferNuGet             # ignore local installs, use the NuGet API packages

  The build machine does NOT need Revit or AutoCAD: when a version is not installed, its API
  comes from NuGet (Nice3point.Revit.Api.* / AutoCAD.NET.Model + AutoCAD.NET.Core, reference
  assemblies only). When it is installed, the local DLLs are used and the .NET target is read
  from them (a service pack can move a release to a newer runtime).

    Revit 2024 -> net48     Revit 2025/2026 -> net8.0-windows     Revit 2027+ -> net10.0-windows
    AutoCAD 2021-2024 -> net48   2025/2026 -> net8.0-windows   2027+ -> net10.0-windows

  Layout produced (per Revit version):
    dist\Revit2025\JA.addin
    dist\Revit2025\JA\JA.Revit.dll                                     (abas JA Famílias e JA Tools)
    dist\Revit2025\JA\MergeEngine\<tfm>\RevitDwgUnifier.AcadMerge.dll
#>
param(
  [string[]]$RevitVersions = @("2024", "2025", "2026", "2027"),
  [string]$Configuration   = "Release",
  [switch]$Deploy,
  [switch]$PreferNuGet
)
$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$env:DOTNET_CLI_UI_LANGUAGE = "pt-BR"
$root = Split-Path $PSScriptRoot -Parent
$bin  = Join-Path $root "bin\$Configuration"
$dist = Join-Path $root "dist"
$addins = @(
  @{ Name = "JA para Revit"; Folder = "JA"; Proj = "$root\src\JA.Revit\JA.Revit.csproj" }
)
$acadProj  = "$root\src\RevitDwgUnifier.AcadMerge\RevitDwgUnifier.AcadMerge.csproj"

# Returns the TFM (net48 / net8.0-windows / net10.0-windows ...) matching the runtime that
# a host API assembly was compiled against, or $null if it cannot be read.
function Get-HostTargetFramework([string]$dll) {
  $major = $null
  try {
    $stream = [System.IO.File]::OpenRead($dll)
    try {
      $pe = New-Object System.Reflection.PortableExecutable.PEReader($stream)
      $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
      foreach ($h in $md.AssemblyReferences) {
        $ref = $md.GetAssemblyReference($h)
        $name = $md.GetString($ref.Name)
        if ($name -eq "System.Runtime" -or $name -eq "netstandard") { $major = $ref.Version.Major; break }
        if ($name -eq "mscorlib" -and -not $major) { $major = 4 }
      }
    } finally { $stream.Dispose() }
  } catch {
    try {
      $asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($dll)
      foreach ($ref in $asm.GetReferencedAssemblies()) {
        if ($ref.Name -eq "System.Runtime") { $major = $ref.Version.Major; break }
        if ($ref.Name -eq "mscorlib" -and -not $major) { $major = 4 }
      }
    } catch { return $null }
  }
  if (-not $major) { return $null }
  if ($major -le 4) { return "net48" }
  return "net$major.0-windows"
}

function Get-FallbackTfm([int]$year) {
  if ($year -ge 2027) { return "net10.0-windows" }
  if ($year -ge 2025) { return "net8.0-windows" }
  return "net48"
}

# Stale obj\ folders from another target/package set confuse the restore: start clean.
function Clear-Obj([string]$project) {
  $obj = Join-Path (Split-Path $project -Parent) "obj"
  if (Test-Path $obj) { Remove-Item $obj -Recurse -Force -ErrorAction SilentlyContinue }
}

function Invoke-DotnetBuild([string]$project, [string[]]$props) {
  Clear-Obj $project
  dotnet build $project -c $Configuration @props -clp:ErrorsOnly | Out-Host
  return ($LASTEXITCODE -eq 0)
}

$failures = @()
$warnings = @()

# ================================================================ merge engines
# One engine per AutoCAD API release and .NET runtime, in MergeEngine\R<release>-<tfm>\.
# Each is compiled against the OLDEST release of its group, so it also loads in the newer ones.
# AutoCAD 2025/2026 (R25) started on .NET 8 and later service packs moved to .NET 10, and
# Autodesk's current AutoCAD.NET 25.x packages only target net10: both engines are built when
# possible; at run time the add-in tries the .NET 8 one first and falls back to .NET 10.
$families = @(
  @{ Folder = "R24-net48";           Tfm = "net48";           Years = @("2021", "2022", "2023", "2024"); NuGet = @("24.0.*", "24.1.*", "24.2.*", "24.3.*") },
  @{ Folder = "R25-net8.0-windows";  Tfm = "net8.0-windows";  Years = @("2025", "2026");                 NuGet = @("[25.0.0]", "[25.1.0]") },
  @{ Folder = "R25-net10.0-windows"; Tfm = "net10.0-windows"; Years = @("2025", "2026");                 NuGet = @("25.0.*", "25.1.*") },
  @{ Folder = "R26-net10.0-windows"; Tfm = "net10.0-windows"; Years = @("2027");                         NuGet = @("26.0.*", "26.1.*") }
)
$engines = [ordered]@{}
if (Test-Path "$bin\MergeEngine") { Remove-Item "$bin\MergeEngine" -Recurse -Force }
foreach ($f in $families) {
  $tfm = $f.Tfm
  $folder = $f.Folder
  $done = $false
  if (-not $PreferNuGet) {
    foreach ($y in $f.Years) {
      $refDir = "C:\Program Files\Autodesk\AutoCAD $y"
      if (-not (Test-Path "$refDir\acdbmgd.dll")) { continue }
      if ((Get-HostTargetFramework "$refDir\acdbmgd.dll") -ne $tfm) { continue }
      Write-Host "Motor de uniao $folder  (AutoCAD $y instalado)" -ForegroundColor Cyan
      if (Invoke-DotnetBuild $acadProj @("-p:AcadVersion=$y", "-p:AcadRefDir=$refDir", "-p:AcadTargetFramework=$tfm", "-p:UseAcadNuGet=false", "-p:EngineFolder=$folder")) {
        $engines[$folder] = "AutoCAD $y (local)"; $done = $true; break
      }
    }
  }
  if (-not $done) {
    foreach ($v in $f.NuGet) {
      Write-Host "Motor de uniao $folder  (NuGet AutoCAD.NET $v)" -ForegroundColor Cyan
      if (Invoke-DotnetBuild $acadProj @("-p:AcadTargetFramework=$tfm", "-p:UseAcadNuGet=true", "-p:AcadNuGetVersion=$v", "-p:AcadRefDir=C:\__none__", "-p:EngineFolder=$folder")) {
        $engines[$folder] = "NuGet $v"; $done = $true; break
      }
    }
  }
  if (-not $done) { $warnings += "Motor de uniao $folder nao foi gerado (sem AutoCAD local compativel e sem pacote NuGet para $tfm)." }
}
if (-not ($engines.Keys | Where-Object { $_ -like "R25-*" })) {
  $warnings += "AutoCAD 2025/2026: nenhum motor gerado; nesses AutoCAD o DWG unico nao funcionara."
}
if ($engines.Count -eq 0) { $warnings += "Nenhum motor de uniao gerado: o DWG unico nao funcionara." }

# ================================================================ Revit add-ins (both, per version)
$builtRevit = @()
foreach ($rv in $RevitVersions) {
  $installDir = "C:\Program Files\Autodesk\Revit $rv"
  $api = "$installDir\RevitAPI.dll"
  $local = (-not $PreferNuGet) -and (Test-Path $api)
  $tfm = $null
  if ($local) { $tfm = Get-HostTargetFramework $api }
  if (-not $tfm) { $tfm = Get-FallbackTfm ([int]$rv) }

  $props = @("-p:RevitVersion=$rv", "-p:RevitTargetFramework=$tfm")
  if ($local) { $props += "-p:UseRevitNuGet=false" }
  else { $props += @("-p:UseRevitNuGet=true", "-p:RevitInstallDir=C:\__none__") }
  $okAll = $true
  foreach ($a in $addins) {
    if ($local) { Write-Host "$($a.Name) para Revit $rv  ($tfm, Revit instalado)" -ForegroundColor Cyan }
    else { Write-Host "$($a.Name) para Revit $rv  ($tfm, NuGet Nice3point.Revit.Api $rv.*)" -ForegroundColor Cyan }
    if (-not (Invoke-DotnetBuild $a.Proj $props)) {
      $okAll = $false
      if ($local) { $failures += "$($a.Name) - Revit $rv" }
      else { $warnings += "$($a.Name): Revit $rv nao incluido (pacote NuGet $rv.* indisponivel ou SDK sem suporte a $tfm)." }
    }
  }
  if (-not $okAll) { continue }
  $builtRevit += $rv

  $target = Join-Path $dist "Revit$rv"
  if (Test-Path $target) { Remove-Item $target -Recurse -Force }
  New-Item -ItemType Directory -Force $target | Out-Null
  Copy-Item "$root\src\JA.addin" $target
  foreach ($a in $addins) {
    New-Item -ItemType Directory -Force "$target\$($a.Folder)" | Out-Null
    Copy-Item "$bin\Revit$rv\$($a.Folder)\*" "$target\$($a.Folder)" -Recurse
    # host API assemblies are provided by Revit: never ship them
    Get-ChildItem "$target\$($a.Folder)" -Recurse -Include "RevitAPI*.dll", "AdWindows.dll", "UIFramework*.dll" -ErrorAction SilentlyContinue | Remove-Item -Force
  }
  if (Test-Path "$bin\MergeEngine") {
    Copy-Item "$bin\MergeEngine" "$target\JA\MergeEngine" -Recurse
    Get-ChildItem "$target\JA\MergeEngine" -Recurse -Include "acdbmgd.dll", "accoremgd.dll", "acmgd.dll", "AcCoreMgd.dll", "AcDbMgd.dll" -ErrorAction SilentlyContinue | Remove-Item -Force
  }

  if ($Deploy) {
    $dest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$rv"
    New-Item -ItemType Directory -Force $dest | Out-Null
    # manifests of the old separate installs would load the same add-ins twice
    foreach ($old in @("JAEsquadrias.addin", "RevitDwgUnifier.addin")) { $f = Join-Path $dest $old; if (Test-Path $f) { Remove-Item $f -Force } }
    foreach ($old in @("JAEsquadrias", "RevitDwgUnifier")) { $f = Join-Path $dest $old; if (Test-Path $f) { Remove-Item $f -Recurse -Force -ErrorAction SilentlyContinue } }
    Copy-Item "$target\*" $dest -Recurse -Force
    Write-Host "Instalado em $dest" -ForegroundColor Green
  }
}

Write-Host ""
Write-Host ("Revit incluidos: " + ($(if ($builtRevit.Count) { $builtRevit -join ", " } else { "nenhum" })))
if ($engines.Count) { Write-Host ("Motores de uniao: " + (($engines.GetEnumerator() | ForEach-Object { "$($_.Key) <- $($_.Value)" }) -join "; ")) }
foreach ($w in $warnings) { Write-Warning $w }
if ($failures.Count) {
  Write-Host "Falhou:" -ForegroundColor Red
  $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
  throw "A compilacao falhou: $($failures.Count) item(ns). Veja os erros acima."
}
if ($builtRevit.Count -eq 0) { throw "Nenhuma versao do Revit foi compilada." }
