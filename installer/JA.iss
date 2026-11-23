; =====================================================================================
;  Instalador do JA para Revit — abas JA Famílias e JA Tools  (Inno Setup 6.3 ou superior)
;
;  Nao compile este arquivo diretamente: rode  GerarInstalador.bat  (ou
;  build\make-installer.ps1), que primeiro compila os dois add-ins e o motor de uniao
;  para a pasta  dist\  e depois chama o ISCC.exe com este script.
;  Resultado:  dist\Instalador\JA_Setup_<versao>.exe
;
;  O que o Setup.exe faz em qualquer computador:
;   * pergunta se instala so para o usuario (sem administrador) ou para todos os usuarios:
;       usuario:  %AppData%\Autodesk\Revit\Addins\<versao>\
;       todos:    %ProgramData%\Autodesk\Revit\Addins\<versao>\         (Revit 2024 a 2026)
;                 C:\Program Files\Autodesk\Revit\Addins\<versao>\    (Revit 2027 em diante)
;     somente nas versoes do Revit (2024 a 2027) encontradas no computador (ou em todas, se
;     o usuario pedir, para um Revit que sera instalado depois);
;   * um add-in (pasta JA, JA.Revit.dll) e um manifesto (JA.addin) com as duas abas: "JA Famílias" e "JA Tools";
;   * leva junto o motor de uniao do DWG para cada familia do AutoCAD e as familias de detalhe
;     tipico e identificadores do escritorio (em %AppData%\RevitDwgUnifier);
;     .NET: o Revit 2024 usa o .NET Framework 4.8 do Windows 10/11; o Revit 2025+ traz o
;     proprio .NET. Nada mais precisa ser instalado;
;   * desinstala os instaladores separados antigos (JA Tools 1.x e JA Famílias 1.x) e remove
;     uma instalacao anterior antes de copiar (sem perder bibliotecas, modelos e preferencias);
;   * avisa se o Revit estiver aberto e se nao houver AutoCAD (o DWG unico precisa dele);
;   * cria o desinstalador em "Aplicativos instalados" do Windows.
; =====================================================================================

#ifndef AppVersion
  #define AppVersion "2.6.2"
#endif
#define AppName   "JA para Revit"
#define Dist      "..\dist"

#define Has2024 DirExists(AddBackslash(SourcePath) + Dist + "\Revit2024")
#define Has2025 DirExists(AddBackslash(SourcePath) + Dist + "\Revit2025")
#define Has2026 DirExists(AddBackslash(SourcePath) + Dist + "\Revit2026")
#define Has2027 DirExists(AddBackslash(SourcePath) + Dist + "\Revit2027")
#define HasFamilies DirExists(AddBackslash(SourcePath) + "..\content\Familias")
#define HasTags DirExists(AddBackslash(SourcePath) + "..\content\Identificadores")

#if !Has2024 && !Has2025 && !Has2026 && !Has2027
  #error Nenhuma build encontrada em dist\. Rode GerarInstalador.bat em vez de compilar este .iss diretamente.
#endif

[Setup]
AppId={{8C1E5F2A-6B3D-4A7E-9F10-2D4C6B8A0E35}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Joaquim Andrade Arquitetos Associados
AppComments=Ferramentas e famílias do escritório para o Revit (abas JA Famílias e JA Tools)
; the JA Tools content (detail families, tags) lives here: the add-in looks for it in this folder
DefaultDirName={autoappdata}\RevitDwgUnifier
DisableDirPage=yes
DisableProgramGroupPage=yes
; padrao: so para o usuario, sem administrador; a primeira tela deixa escolher "todos os usuarios"
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#Dist}\Instalador
OutputBaseFilename=JA_Setup_{#AppVersion}
SetupIconFile=JATools.ico
WizardImageFile=wizard_164.bmp,wizard_246.bmp,wizard_410.bmp
WizardSmallImageFile=wizard_small_55.bmp,wizard_small_83.bmp,wizard_small_138.bmp
WizardImageStretch=no
UninstallDisplayIcon={app}\JATools.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Nunca fechar o Revit automaticamente (pode haver trabalho nao salvo): o instalador avisa.
CloseApplications=no
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Joaquim Andrade Arquitetos Associados
VersionInfoDescription=Instalador do JA para Revit (JA Famílias e JA Tools)

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en";   MessagesFile: "compiler:Default.isl"

[Files]
#if Has2024
Source: "{#Dist}\Revit2024\JA.addin"; DestDir: "{code:AddinDir|2024}"; Flags: ignoreversion; Check: ShouldInstall('2024')
Source: "{#Dist}\Revit2024\JA\*"; DestDir: "{code:AddinDir|2024}\JA"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: ShouldInstall('2024')
#endif
#if Has2025
Source: "{#Dist}\Revit2025\JA.addin"; DestDir: "{code:AddinDir|2025}"; Flags: ignoreversion; Check: ShouldInstall('2025')
Source: "{#Dist}\Revit2025\JA\*"; DestDir: "{code:AddinDir|2025}\JA"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: ShouldInstall('2025')
#endif
#if Has2026
Source: "{#Dist}\Revit2026\JA.addin"; DestDir: "{code:AddinDir|2026}"; Flags: ignoreversion; Check: ShouldInstall('2026')
Source: "{#Dist}\Revit2026\JA\*"; DestDir: "{code:AddinDir|2026}\JA"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: ShouldInstall('2026')
#endif
#if Has2027
Source: "{#Dist}\Revit2027\JA.addin"; DestDir: "{code:AddinDir|2027}"; Flags: ignoreversion; Check: ShouldInstall('2027')
Source: "{#Dist}\Revit2027\JA\*"; DestDir: "{code:AddinDir|2027}\JA"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: ShouldInstall('2027')
#endif
#if HasTags
; identificadores (familias de etiqueta) do escritorio, carregados sob demanda pelas ferramentas de etiqueta
Source: "..\content\Identificadores\*"; DestDir: "{app}\Identificadores"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
#if HasFamilies
; familias de detalhe tipico (Det.Frontispicio, Det.Saia, Det.Tira Americana...) usadas pelo Gerar Detalhes
Source: "..\content\Familias\*"; DestDir: "{app}\Familias"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
Source: "JATools.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "JAEsquadrias.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "Diagnostico.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Diagnostico.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "LEIA-ME.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\JA para Revit\Familias de detalhe"; Filename: "{app}\Familias"
Name: "{autoprograms}\JA para Revit\Identificadores"; Filename: "{app}\Identificadores"
Name: "{autoprograms}\JA para Revit\Leia-me"; Filename: "{app}\LEIA-ME.txt"
Name: "{autoprograms}\JA para Revit\Diagnostico"; Filename: "{app}\Diagnostico.bat"; WorkingDir: "{app}"; IconFilename: "{app}\JATools.ico"
Name: "{autoprograms}\JA para Revit\Desinstalar JA para Revit"; Filename: "{uninstallexe}"

[InstallDelete]
; versao anterior (inclusive os dois add-ins separados de antes da 2.0: pastas JAEsquadrias e RevitDwgUnifier)
Type: filesandordirs; Name: "{code:AddinDir|2024}\JA"; Check: ShouldInstall('2024')
Type: files; Name: "{code:AddinDir|2024}\JA.addin"; Check: ShouldInstall('2024')
Type: filesandordirs; Name: "{code:AddinDir|2024}\JAEsquadrias"; Check: ShouldInstall('2024')
Type: files; Name: "{code:AddinDir|2024}\JAEsquadrias.addin"; Check: ShouldInstall('2024')
Type: filesandordirs; Name: "{code:AddinDir|2024}\RevitDwgUnifier"; Check: ShouldInstall('2024')
Type: files; Name: "{code:AddinDir|2024}\RevitDwgUnifier.addin"; Check: ShouldInstall('2024')
Type: filesandordirs; Name: "{code:AddinDir|2025}\JA"; Check: ShouldInstall('2025')
Type: files; Name: "{code:AddinDir|2025}\JA.addin"; Check: ShouldInstall('2025')
Type: filesandordirs; Name: "{code:AddinDir|2025}\JAEsquadrias"; Check: ShouldInstall('2025')
Type: files; Name: "{code:AddinDir|2025}\JAEsquadrias.addin"; Check: ShouldInstall('2025')
Type: filesandordirs; Name: "{code:AddinDir|2025}\RevitDwgUnifier"; Check: ShouldInstall('2025')
Type: files; Name: "{code:AddinDir|2025}\RevitDwgUnifier.addin"; Check: ShouldInstall('2025')
Type: filesandordirs; Name: "{code:AddinDir|2026}\JA"; Check: ShouldInstall('2026')
Type: files; Name: "{code:AddinDir|2026}\JA.addin"; Check: ShouldInstall('2026')
Type: filesandordirs; Name: "{code:AddinDir|2026}\JAEsquadrias"; Check: ShouldInstall('2026')
Type: files; Name: "{code:AddinDir|2026}\JAEsquadrias.addin"; Check: ShouldInstall('2026')
Type: filesandordirs; Name: "{code:AddinDir|2026}\RevitDwgUnifier"; Check: ShouldInstall('2026')
Type: files; Name: "{code:AddinDir|2026}\RevitDwgUnifier.addin"; Check: ShouldInstall('2026')
Type: filesandordirs; Name: "{code:AddinDir|2027}\JA"; Check: ShouldInstall('2027')
Type: files; Name: "{code:AddinDir|2027}\JA.addin"; Check: ShouldInstall('2027')
Type: filesandordirs; Name: "{code:AddinDir|2027}\JAEsquadrias"; Check: ShouldInstall('2027')
Type: files; Name: "{code:AddinDir|2027}\JAEsquadrias.addin"; Check: ShouldInstall('2027')
Type: filesandordirs; Name: "{code:AddinDir|2027}\RevitDwgUnifier"; Check: ShouldInstall('2027')
Type: files; Name: "{code:AddinDir|2027}\RevitDwgUnifier.addin"; Check: ShouldInstall('2027')
; Revit 2027+ ignora manifestos "para todos" em ProgramData: copias antigas de la saem
Type: filesandordirs; Name: "{commonappdata}\Autodesk\Revit\Addins\2027\JA"; Check: IsAdminInstallMode
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2027\JA.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{commonappdata}\Autodesk\Revit\Addins\2027\JAEsquadrias"; Check: IsAdminInstallMode
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2027\JAEsquadrias.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{commonappdata}\Autodesk\Revit\Addins\2027\RevitDwgUnifier"; Check: IsAdminInstallMode
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2027\RevitDwgUnifier.addin"; Check: IsAdminInstallMode
; instalacao para todos: remove a copia so deste usuario (o mesmo AddInId duas vezes confunde o Revit)
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2024\JA"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\JA.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2024\JAEsquadrias"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\JAEsquadrias.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitDwgUnifier"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitDwgUnifier.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\JA"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\JA.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\JAEsquadrias"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\JAEsquadrias.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitDwgUnifier"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitDwgUnifier.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\JA"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\JA.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\JAEsquadrias"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\JAEsquadrias.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitDwgUnifier"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitDwgUnifier.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2027\JA"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2027\JA.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2027\JAEsquadrias"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2027\JAEsquadrias.addin"; Check: IsAdminInstallMode
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2027\RevitDwgUnifier"; Check: IsAdminInstallMode
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2027\RevitDwgUnifier.addin"; Check: IsAdminInstallMode

[UninstallDelete]
; somente os binarios; bibliotecas, familias geradas, configuracoes, modelos e logs do usuario sao preservados
Type: filesandordirs; Name: "{code:AddinDir|2024}\JA"
Type: files; Name: "{code:AddinDir|2024}\JA.addin"
Type: filesandordirs; Name: "{code:AddinDir|2025}\JA"
Type: files; Name: "{code:AddinDir|2025}\JA.addin"
Type: filesandordirs; Name: "{code:AddinDir|2026}\JA"
Type: files; Name: "{code:AddinDir|2026}\JA.addin"
Type: filesandordirs; Name: "{code:AddinDir|2027}\JA"
Type: files; Name: "{code:AddinDir|2027}\JA.addin"
Type: filesandordirs; Name: "{app}\Familias"
Type: filesandordirs; Name: "{app}\Identificadores"

[CustomMessages]
ptbr.NoRevit=Nenhuma versao do Revit compativel (%1) foi encontrada neste computador.%n%nInstalar mesmo assim em todas essas versoes (para um Revit que sera instalado depois)?
en.NoRevit=No supported Revit version (%1) was found on this computer.%n%nInstall anyway for all of them (for a Revit installed later)?
ptbr.RevitRunning=O Revit esta aberto.%n%nSalve o trabalho e feche o Revit antes de continuar: as abas JA Famílias e JA Tools so aparecem depois que o Revit e aberto de novo, e uma versao antiga em uso nao pode ser substituida.%n%nContinuar mesmo assim?
en.RevitRunning=Revit is running.%n%nSave your work and close Revit before continuing.%n%nContinue anyway?
ptbr.NoAutoCAD=O AutoCAD (2021 ou superior, completo ou vertical como o Civil 3D) nao foi encontrado.%n%nO JA para Revit sera instalado normalmente; so o "DWG unico" do Exportar Pranchas precisa do AutoCAD (sem ele, os DWGs saem um por prancha). O AutoCAD LT nao serve.
en.NoAutoCAD=AutoCAD (2021 or newer, full or a vertical such as Civil 3D) was not found.%n%nJA will be installed; only the single-DWG export needs AutoCAD. AutoCAD LT cannot be used.
ptbr.Done=JA para Revit instalado para: %1%n%nAbra o Revit e procure as abas "JA Famílias" e "JA Tools".%nFamilias de detalhe tipico: %2
en.Done=JA installed for: %1%n%nStart Revit and look for the "JA Famílias" and "JA Tools" tabs.%nTypical detail families: %2
ptbr.CommonCopy=Ja existe uma instalacao do JA para TODOS os usuarios deste computador.%n%nInstalar tambem so para voce cria uma copia duplicada (o Revit carrega apenas uma). Recomendado: cancele e rode o instalador de novo escolhendo "Instalar para todos os usuarios".
en.CommonCopy=JA is already installed for ALL users on this computer. Installing for you only creates a duplicate; run the installer again and choose "Install for all users".

[Code]
var
  ForceAll: Boolean;

function IncludedVersions(): String;
begin
  Result := '';
#if Has2024
  Result := Result + 'Revit 2024  ';
#endif
#if Has2025
  Result := Result + 'Revit 2025  ';
#endif
#if Has2026
  Result := Result + 'Revit 2026  ';
#endif
#if Has2027
  Result := Result + 'Revit 2027  ';
#endif
end;

{ Pasta de add-ins de uma versao do Revit.
    so para mim:            %AppData%\Autodesk\Revit\Addins\<versao>
    todos, 2024 a 2026:     %ProgramData%\Autodesk\Revit\Addins\<versao>
    todos, 2027 em diante:  C:\Program Files\Autodesk\Revit\Addins\<versao> (ProgramData e ignorado) }
function AddinDir(Version: String): String;
begin
  if IsAdminInstallMode() and (StrToIntDef(Version, 0) >= 2027) then
    Result := ExpandConstant('{commonpf64}\Autodesk\Revit\Addins\') + Version
  else
    Result := ExpandConstant('{autoappdata}\Autodesk\Revit\Addins\') + Version;
end;

{ Revit esta instalado se o executavel existe, se a chave do Revit existe no registro ou se a
  pasta de add-ins ja existe (instalacoes em outro drive tambem criam essas pastas). }
function RevitInstalled(Version: String): Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\Autodesk\Revit ' + Version + '\Revit.exe'))
         or RegKeyExists(HKLM64, 'SOFTWARE\Autodesk\Revit\' + Version)
         or RegKeyExists(HKLM64, 'SOFTWARE\Autodesk\Revit\Autodesk Revit ' + Version)
         or DirExists(ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\' + Version))
         or DirExists(ExpandConstant('{commonpf64}\Autodesk\Revit\Addins\' + Version))
         or DirExists(ExpandConstant('{userappdata}\Autodesk\Revit\Addins\' + Version));
end;

function ShouldInstall(Version: String): Boolean;
begin
  Result := ForceAll or RevitInstalled(Version);
end;

function AnyTargetInstalled(): Boolean;
begin
  Result := False;
#if Has2024
  Result := Result or RevitInstalled('2024');
#endif
#if Has2025
  Result := Result or RevitInstalled('2025');
#endif
#if Has2026
  Result := Result or RevitInstalled('2026');
#endif
#if Has2027
  Result := Result or RevitInstalled('2027');
#endif
end;

function AutoCadFound(): Boolean;
var
  Year: Integer;
  Names: TArrayOfString;
begin
  Result := False;
  for Year := 2021 to 2035 do
    if FileExists(ExpandConstant('{commonpf64}\Autodesk\AutoCAD ' + IntToStr(Year) + '\accoreconsole.exe')) then
    begin
      Result := True;
      Exit;
    end;
  if RegGetSubkeyNames(HKLM64, 'SOFTWARE\Autodesk\AutoCAD', Names) then
    Result := GetArrayLength(Names) > 0;
end;

{ Revit aberto? (tasklist, sem depender de WMI) }
function RevitRunning(): Boolean;
var
  ResultCode: Integer;
  OutFile: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  OutFile := ExpandConstant('{tmp}\ja_tasks.txt');
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq Revit.exe" /NH > "' + OutFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    if LoadStringsFromFile(OutFile, Lines) then
      for I := 0 to GetArrayLength(Lines) - 1 do
        if Pos('revit.exe', Lowercase(Lines[I])) > 0 then
        begin
          Result := True;
          Exit;
        end;
end;

{ uma instalacao "para todos os usuarios" ja existe: duas copias com o mesmo AddInId confundem o Revit }
function CommonCopyExists(): Boolean;
var
  Year: Integer;
  Nome: Integer;
  Arq: String;
begin
  Result := False;
  for Year := 2024 to 2028 do
    for Nome := 0 to 2 do
    begin
      if Nome = 0 then Arq := 'JA.addin' else if Nome = 1 then Arq := 'RevitDwgUnifier.addin' else Arq := 'JAEsquadrias.addin';
      if FileExists(ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\' + IntToStr(Year) + '\' + Arq)) or
         FileExists(ExpandConstant('{commonpf64}\Autodesk\Revit\Addins\' + IntToStr(Year) + '\' + Arq)) then
        Result := True;
    end;
end;

{ Os instaladores separados de antes da 2.0 (JA Tools 1.x e JA Famílias 1.x): o desinstalador
  deles roda em silencio antes da copia, para nao ficarem duas entradas em "Aplicativos
  instalados" (desinstalar uma delas depois apagaria a instalacao nova). Configuracoes,
  bibliotecas e modelos do usuario ficam (esses desinstaladores so apagam binarios). }
procedure DesinstalarAntigo(AppIdAntigo: String);
var
  Chave, Cmd: String;
  Raiz, ResultCode: Integer;
begin
  Chave := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + AppIdAntigo + '_is1';
  for Raiz := 0 to 1 do
  begin
    Cmd := '';
    if Raiz = 0 then
    begin
      if not RegQueryStringValue(HKCU, Chave, 'UninstallString', Cmd) then Cmd := '';
    end
    else
    begin
      if IsAdminInstallMode() then
        if not RegQueryStringValue(HKLM64, Chave, 'UninstallString', Cmd) then
          if not RegQueryStringValue(HKLM32, Chave, 'UninstallString', Cmd) then Cmd := '';
    end;
    Cmd := RemoveQuotes(Cmd);
    if (Cmd <> '') and FileExists(Cmd) then
      Exec(Cmd, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  DesinstalarAntigo('{48046ECB-ADA6-4253-ABC8-F95936C3D941}');   { JA Tools 1.x }
  DesinstalarAntigo('{4D35B835-DEBF-4C29-9139-C62A19D07BE1}');   { JA Famílias 1.x }
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  ForceAll := False;
  if not AnyTargetInstalled() then
  begin
    if WizardSilent() then
      ForceAll := True
    else if MsgBox(FmtMessage(CustomMessage('NoRevit'), [IncludedVersions()]), mbConfirmation, MB_YESNO) = IDYES then
      ForceAll := True
    else
    begin
      Result := False;
      Exit;
    end;
  end;
  if (not WizardSilent()) and RevitRunning() then
    if MsgBox(CustomMessage('RevitRunning'), mbConfirmation, MB_YESNO) <> IDYES then
    begin
      Result := False;
      Exit;
    end;
  if (not WizardSilent()) and (not IsAdminInstallMode()) and CommonCopyExists() then
    MsgBox(CustomMessage('CommonCopy'), mbInformation, MB_OK);
  if (not WizardSilent()) and (not AutoCadFound()) then
    MsgBox(CustomMessage('NoAutoCAD'), mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Installed: String;
begin
  if (CurStep = ssDone) and not WizardSilent() then
  begin
    Installed := '';
#if Has2024
    if ShouldInstall('2024') then Installed := Installed + 'Revit 2024  ';
#endif
#if Has2025
    if ShouldInstall('2025') then Installed := Installed + 'Revit 2025  ';
#endif
#if Has2026
    if ShouldInstall('2026') then Installed := Installed + 'Revit 2026  ';
#endif
#if Has2027
    if ShouldInstall('2027') then Installed := Installed + 'Revit 2027  ';
#endif
    MsgBox(FmtMessage(CustomMessage('Done'), [Installed, ExpandConstant('{app}\Familias')]), mbInformation, MB_OK);
  end;
end;
