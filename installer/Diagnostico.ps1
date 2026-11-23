<#
  JA Tools - diagnostico da instalacao.
  Gera JATools_Diagnostico.txt na Area de Trabalho com tudo o que e preciso para descobrir por que
  o JA Tools nao aparece num Revit: manifestos .addin (usuario e todos os usuarios), DLLs,
  registros do JA Tools e as linhas dos journals do Revit que falam do add-in.
#>
$ErrorActionPreference = "Continue"
$out = Join-Path ([Environment]::GetFolderPath("Desktop")) "JATools_Diagnostico.txt"
$sb = New-Object System.Text.StringBuilder
function L([string]$t) { [void]$sb.AppendLine($t) }

L "JA Tools - diagnostico  $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
L "Computador: $env:COMPUTERNAME   Usuario: $env:USERNAME"
L "Windows: $([Environment]::OSVersion.VersionString)"
L ""

$roots = @(
  @{ Name = "usuario (AppData)";        Path = Join-Path $env:APPDATA "Autodesk\Revit\Addins" },
  @{ Name = "todos (ProgramData)";      Path = Join-Path $env:ProgramData "Autodesk\Revit\Addins" },
  @{ Name = "todos (Program Files, 2027+)"; Path = Join-Path $env:ProgramFiles "Autodesk\Revit\Addins" }
)
foreach ($v in 2024..2028) {
  $exe = "C:\Program Files\Autodesk\Revit $v\Revit.exe"
  $hasExe = Test-Path $exe
  L "=== Revit $v  (Revit.exe: $(if ($hasExe) { (Get-Item $exe).VersionInfo.ProductVersion } else { 'nao encontrado' }))"
  foreach ($r in $roots) {
    $dir = Join-Path $r.Path "$v"
    if (-not (Test-Path $dir)) { L "  [$($r.Name)] pasta nao existe: $dir"; continue }
    $manifests = Get-ChildItem $dir -Filter *.addin -ErrorAction SilentlyContinue
    $ours = @()
    foreach ($m in $manifests) {
      $txt = Get-Content $m.FullName -Raw -ErrorAction SilentlyContinue
      if ($txt -match 'RevitDwgUnifier|48046ECB|JAEsquadrias|4F9ED87E') { $ours += $m }
    }
    if (-not $ours) { L "  [$($r.Name)] sem manifesto do JA Tools em $dir  ($($manifests.Count) outro(s) .addin)" }
    foreach ($m in $ours) {
      L "  [$($r.Name)] manifesto: $($m.FullName)  ($($m.LastWriteTime))"
      [xml]$x = Get-Content $m.FullName -Raw
      foreach ($a in $x.RevitAddIns.AddIn) {
        $asm = $a.Assembly
        $full = if ([IO.Path]::IsPathRooted($asm)) { $asm } else { Join-Path $dir $asm }
        if (Test-Path $full) {
          $fi = Get-Item $full
          L "      DLL: $full  ($([math]::Round($fi.Length/1kb)) KB, $($fi.LastWriteTime), versao $($fi.VersionInfo.FileVersion))"
        } else { L "      DLL NAO ENCONTRADA: $full" }
        L "      Classe: $($a.FullClassName)   AddInId: $($a.AddInId)"
      }
      $eng = Join-Path $dir "JA\MergeEngine"
      if (Test-Path $eng) { L "      Motores: $((Get-ChildItem $eng -Directory | ForEach-Object Name) -join ', ')" }
    }
  }
  # journals: what Revit said about the add-in on its last starts
  $jdir = Join-Path $env:LOCALAPPDATA "Autodesk\Revit\Autodesk Revit $v\Journals"
  if (Test-Path $jdir) {
    $journals = Get-ChildItem $jdir -Filter "journal.*.txt" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3
    foreach ($j in $journals) {
      $hits = Select-String -Path $j.FullName -Pattern 'RevitDwgUnifier|JA Tools|48046ECB|JATools|JAEsquadrias|JA Fam|4F9ED87E|won.t be loaded|must be installed' -ErrorAction SilentlyContinue | Select-Object -First 40
      L "  Journal $($j.Name) ($($j.LastWriteTime)): $(@($hits).Count) linha(s) sobre o JA Tools"
      foreach ($h in $hits) { L ("      " + $h.Line.Trim()) }
    }
  }
  L ""
}

L "=== AutoCAD"
Get-ChildItem "C:\Program Files\Autodesk" -Directory -Filter "AutoCAD 20*" -ErrorAction SilentlyContinue | ForEach-Object {
  $c = Join-Path $_.FullName "accoreconsole.exe"
  L "  $($_.Name): accoreconsole $(if (Test-Path $c) {'ok'} else {'ausente'}), acad.exe $(if (Test-Path (Join-Path $_.FullName 'acad.exe')) {'ok'} else {'ausente'})"
}
L ""

L "=== Registro do JA Tools (ultimas linhas)"
$logs = Join-Path $env:LOCALAPPDATA "RevitDwgUnifier\Logs"
$last = Get-ChildItem $logs -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($last) { L "  $($last.FullName)"; Get-Content $last.FullName -Tail 120 | ForEach-Object { L ("  " + $_) } }
else { L "  nenhum registro em $logs (o JA Tools nunca iniciou neste usuario)" }

[IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))
Start-Process notepad.exe $out
