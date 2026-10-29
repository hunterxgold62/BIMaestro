; --------------------------------------------------------
; BIMaestro.iss — Installateur per-user sans UAC
; installe une DLL compilée pour chaque version de Revit (2022–2027),
; uniquement pour les versions présentes sur le poste :
;   {localappdata}\BIMaestro\Bin\Revit2023  (.NET 4.8, sert aussi à Revit 2022)
;   {localappdata}\BIMaestro\Bin\Revit2024  (.NET 4.8)
;   {localappdata}\BIMaestro\Bin\Revit2025  (.NET 8)
;   {localappdata}\BIMaestro\Bin\Revit2026  (.NET 8)
;   {localappdata}\BIMaestro\Bin\Revit2027  (.NET 10)
; + Copie d’un uninstalleur renommé "Suppression BIMaestro.exe"
;   dans le dossier Addins\2024
; + Exclut les .pdb et les binaires natifs inutiles à Revit (x86, ARM, macOS)
; --------------------------------------------------------

#define AppVersion "1.0.6.5"
#define Branding "..\installer\branding"

[Setup]
AppId={{E3B0C442-98FC-1C14-9AF7-7D7CE11B9A09}}
AppName=BIMaestro
AppVersion={#AppVersion}
AppVerName=BIMaestro {#AppVersion}
AppPublisher=Paul LEMERT
PrivilegesRequired=lowest
; Apparence aux couleurs de BIMaestro (images générées depuis Resources\OLD\BIMaestro.png).
WizardStyle=modern
SetupIconFile={#Branding}\BIMaestro.ico
UninstallDisplayIcon={uninstallexe}
WizardImageFile={#Branding}\wizard-100.bmp,{#Branding}\wizard-150.bmp,{#Branding}\wizard-200.bmp
WizardSmallImageFile={#Branding}\small-100.bmp,{#Branding}\small-150.bmp,{#Branding}\small-200.bmp
ShowLanguageDialog=no
; Fenêtre un peu plus grande pour un texte plus lisible.
WizardSizePercent=115
DisableProgramGroupPage=yes
DisableDirPage=yes
DefaultDirName={localappdata}\BIMaestro\Bin
OutputBaseFilename=BIMaestroInstaller
; Les cinq BIMaestro.dll (~45 Mo chacune) se ressemblent presque entièrement :
; un dictionnaire plus grand qu'une DLL permet de ne les stocker qu'une fois.
Compression=lzma2/ultra64
SolidCompression=yes
LZMADictionarySize=262144
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=1

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[LangOptions]
fr.DialogFontSize=11
fr.WelcomeFontSize=15

[Messages]
fr.WelcomeLabel1=Bienvenue dans BIMaestro {#AppVersion}
fr.WelcomeLabel2=BIMaestro ajoute à Revit ses outils de modélisation, de contrôle et de personnalisation, ainsi que des parcours guidés pour les découvrir.%n%nL'installation ne demande pas de droits administrateur. Ferme Revit avant de continuer.
fr.FinishedHeadingLabel=BIMaestro {#AppVersion} est installé
fr.FinishedLabelNoIcons=Ouvre Revit : BIMaestro apparaît dans son onglet du ruban.%n%nSi Revit demande d'autoriser le complément, choisis « Toujours charger ». Pour démarrer, essaie les « Parcours guidés ».
fr.FinishedLabel=Ouvre Revit : BIMaestro apparaît dans son onglet du ruban.%n%nSi Revit demande d'autoriser le complément, choisis « Toujours charger ». Pour démarrer, essaie les « Parcours guidés ».

#define AppExcludes "*.pdb,BIMaestro - Copie.dll,BIMaestro.Updater.*,*.dylib,\x86\*,\arm64\*"
#define CoreExcludes "*.pdb,BIMaestro - Copie.dll,BIMaestro.Updater.*,\runtimes\win-x86\*,\runtimes\win-arm64\*,\runtimes\osx*,\runtimes\linux*,\runtimes\browser\*"

[Files]
#if !FileExists("bin\Release\BIMaestro.dll")
  #error Compilez BIMaestro.csproj en Release (Revit 2023) avant de creer l'installateur.
#endif
#if !FileExists("bin\Release2024\BIMaestro.dll")
  #error Compilez BIMaestro.csproj en Release2024 avant de creer l'installateur.
#endif
#if !FileExists("bin\Revit2025\Release\BIMaestro.dll")
  #error Compilez BIMaestro.Revit2025.csproj avec RevitVersion=2025.
#endif
#if !FileExists("bin\Revit2026\Release\BIMaestro.dll")
  #error Compilez BIMaestro.Revit2025.csproj avec RevitVersion=2026.
#endif
#if !FileExists("bin\Revit2027\Release\BIMaestro.dll")
  #error Compilez BIMaestro.Revit2025.csproj avec RevitVersion=2027 (SDK .NET 10).
#endif
; La base de formation accompagne chaque DLL (elle est cherchée à côté de la DLL).
#define TrainingBin "bin\Release"
#include "..\Installer\TrainingResources.Checks.iss"
#undef TrainingBin
#define TrainingBin "bin\Release2024"
#include "..\Installer\TrainingResources.Checks.iss"
#undef TrainingBin
#define TrainingBin "bin\Revit2025\Release"
#include "..\Installer\TrainingResources.Checks.iss"
#undef TrainingBin
#define TrainingBin "bin\Revit2026\Release"
#include "..\Installer\TrainingResources.Checks.iss"
#undef TrainingBin
#define TrainingBin "bin\Revit2027\Release"
#include "..\Installer\TrainingResources.Checks.iss"
#undef TrainingBin

; Animation affichée pendant l'installation (non installée sur le poste).
Source: "{#Branding}\mickey-*.bmp"; Flags: dontcopy

; Les fichiers de même nom sont listés côte à côte : la compression solide
; repère ainsi les DLL presque identiques d'une version à l'autre.
Source: "bin\Release\*.*"; DestDir: "{app}\Revit2023"; Check: NeedsRevit2023Build; \
  Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "{#AppExcludes}"
Source: "bin\Release2024\*.*"; DestDir: "{app}\Revit2024"; Check: WantsRevit('2024'); \
  Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "{#AppExcludes}"
Source: "bin\Revit2025\Release\*.*"; DestDir: "{app}\Revit2025"; Check: WantsRevit('2025'); \
  Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "{#CoreExcludes}"
Source: "bin\Revit2026\Release\*.*"; DestDir: "{app}\Revit2026"; Check: WantsRevit('2026'); \
  Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "{#CoreExcludes}"
Source: "bin\Revit2027\Release\*.*"; DestDir: "{app}\Revit2027"; Check: WantsRevit('2027'); \
  Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "{#CoreExcludes}"

[InstallDelete]
; Retirer l'ancien assistant lors d'une mise a jour manuelle.
Type: files; Name: "{app}\BIMaestro.Updater.exe"
Type: files; Name: "{app}\BIMaestro.Updater.exe.config"
Type: files; Name: "{app}\BIMaestro.Updater.pdb"
; Les versions ≤ 1.0.6.5 installaient une DLL commune à la racine de Bin :
; elle n'est plus référencée par aucun .addin.
Type: files;          Name: "{app}\*.dll"
Type: files;          Name: "{app}\*.dylib"
Type: files;          Name: "{app}\*.config"
Type: files;          Name: "{app}\*.json"
Type: files;          Name: "{app}\*.xml"
Type: filesandordirs; Name: "{app}\x64"
Type: filesandordirs; Name: "{app}\x86"
Type: filesandordirs; Name: "{app}\arm64"
Type: filesandordirs; Name: "{app}\Demo"
; Une version de Revit désinstallée ne garde pas son ancienne DLL.
Type: filesandordirs; Name: "{app}\Revit2023"; Check: not NeedsRevit2023Build
Type: filesandordirs; Name: "{app}\Revit2024"; Check: not WantsRevit('2024')
Type: filesandordirs; Name: "{app}\Revit2025"; Check: not WantsRevit('2025')
Type: filesandordirs; Name: "{app}\Revit2026"; Check: not WantsRevit('2026')
Type: filesandordirs; Name: "{app}\Revit2027"; Check: not WantsRevit('2027')

[Code]
const
  MickeyFrameCount = 13;

var
  VersionPage: TInputOptionWizardPage;
  PageYears: TArrayOfString;
  MickeyFrames: array of TBitmapImage;
  MickeyFrame: Integer;
  MickeyTimer: LongWord;

function SetTimer(hWnd: LongWord; nIDEvent, uElapse: LongWord; lpTimerFunc: LongWord): LongWord;
  external 'SetTimer@user32.dll stdcall';
function KillTimer(hWnd: LongWord; uIDEvent: LongWord): BOOL;
  external 'KillTimer@user32.dll stdcall';

// -----------------------------------------------------------------
// Détection des versions de Revit installées. Une clé de registre peut
// survivre à une désinstallation : seule la présence de RevitAPI.dll compte.
// Sources : dossier déclaré dans « Programmes et fonctionnalités »
// (installation hors C:\Program Files comprise), puis dossier par défaut.
// -----------------------------------------------------------------
function HasRevitApi(const Folder: String): Boolean;
begin
  Result := (Folder <> '') and FileExists(AddBackslash(Folder) + 'RevitAPI.dll');
end;

function IsRevitPresent(const Year: String): Boolean;
var
  Names: TArrayOfString;
  i: Integer;
  Key, DisplayName, Location: String;
begin
  Result := HasRevitApi(ExpandConstant('{commonpf64}\Autodesk\Revit ') + Year);
  if Result then Exit;
  if not RegGetSubkeyNames(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', Names) then Exit;
  for i := 0 to GetArrayLength(Names) - 1 do
  begin
    Key := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + Names[i];
    if not RegQueryStringValue(HKLM64, Key, 'DisplayName', DisplayName) then Continue;
    if (DisplayName <> 'Revit ' + Year) and (DisplayName <> 'Autodesk Revit ' + Year) then Continue;
    if not RegQueryStringValue(HKLM64, Key, 'InstallLocation', Location) then Continue;
    // « Autodesk Revit 20xx » déclare le dossier parent (…\Autodesk).
    Result := HasRevitApi(Location) or HasRevitApi(AddBackslash(Location) + 'Revit ' + Year);
    if Result then Exit;
  end;
end;

// Versions cochées sur la page « Versions de Revit ».
function WantsRevit(const Year: String): Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 0 to GetArrayLength(PageYears) - 1 do
    if (PageYears[i] = Year) and VersionPage.Values[i] then
      Result := True;
end;

// Revit 2022 utilise la DLL compilée avec l'API Revit 2023, comme avant.
function NeedsRevit2023Build(): Boolean;
begin
  Result := WantsRevit('2022') or WantsRevit('2023');
end;

function PayloadFolder(const Year: String): String;
begin
  if Year = '2022' then
    Result := 'Revit2023'
  else
    Result := 'Revit' + Year;
end;

// -----------------------------------------------------------------
// Page de choix des versions + couleurs BIMaestro
// -----------------------------------------------------------------
procedure InitializeWizard();
var
  Year, Index: Integer;
  Detected: Boolean;
begin
  VersionPage := CreateInputOptionPage(wpWelcome,
    'Versions de Revit',
    'Pour quelles versions de Revit installer BIMaestro ?',
    'BIMaestro a trouvé ces versions de Revit sur ce poste. Toutes sont cochées : ' +
    'clique sur Suivant pour tout installer, ou décoche celles que tu ne veux pas équiper.',
    False, False);
  Detected := False;
  for Year := 2022 to 2027 do
    if IsRevitPresent(IntToStr(Year)) then
    begin
      Index := VersionPage.Add('Revit ' + IntToStr(Year));
      SetArrayLength(PageYears, Index + 1);
      PageYears[Index] := IntToStr(Year);
      VersionPage.Values[Index] := True;
      Detected := True;
    end;
  if not Detected then
  begin
    // Revit installé à un endroit introuvable : l'utilisateur choisit lui-même.
    VersionPage.SubCaptionLabel.Caption :=
      'Aucune version de Revit 2022 à 2027 n''a été détectée automatiquement. ' +
      'Coche les versions installées sur ce poste.';
    for Year := 2022 to 2027 do
    begin
      Index := VersionPage.Add('Revit ' + IntToStr(Year));
      SetArrayLength(PageYears, Index + 1);
      PageYears[Index] := IntToStr(Year);
    end;
  end;

  // Bandeau des pages intérieures au vert BIMaestro (#105638), textes crème.
  WizardForm.MainPanel.Color := $00385610;
  WizardForm.PageNameLabel.Font.Color := clWhite;
  WizardForm.PageDescriptionLabel.Font.Color := $00E6F1F6;
  WizardForm.WelcomeLabel1.Font.Color := $00385610;
  WizardForm.FinishedHeadingLabel.Font.Color := $00385610;
end;

// -----------------------------------------------------------------
// Animation pendant l'installation : une image par trame, une seule visible.
// -----------------------------------------------------------------
procedure MickeyTick(Wnd: LongWord; Msg: LongWord; IDEvent: LongWord; Time: LongWord);
begin
  if GetArrayLength(MickeyFrames) = 0 then Exit;
  MickeyFrames[MickeyFrame].Visible := False;
  MickeyFrame := (MickeyFrame + 1) mod GetArrayLength(MickeyFrames);
  MickeyFrames[MickeyFrame].Visible := True;
end;

procedure StopMickey();
begin
  if MickeyTimer <> 0 then KillTimer(0, MickeyTimer);
  MickeyTimer := 0;
end;

procedure CreateMickey();
var
  i: Integer;
  Name: String;
  Gauge: TNewProgressBar;
begin
  Gauge := WizardForm.ProgressGauge;
  SetArrayLength(MickeyFrames, MickeyFrameCount);
  for i := 0 to MickeyFrameCount - 1 do
  begin
    Name := Format('mickey-%.2d.bmp', [i]);
    ExtractTemporaryFile(Name);
    MickeyFrames[i] := TBitmapImage.Create(WizardForm);
    MickeyFrames[i].Parent := WizardForm.InstallingPage;
    MickeyFrames[i].AutoSize := True;
    MickeyFrames[i].Bitmap.LoadFromFile(ExpandConstant('{tmp}\') + Name);
    MickeyFrames[i].Left := Gauge.Left + (Gauge.Width - MickeyFrames[i].Width) div 2;
    MickeyFrames[i].Top := Gauge.Top + Gauge.Height + ScaleY(24);
    MickeyFrames[i].Visible := i = 0;
  end;
  MickeyFrame := 0;
end;

// Texte de fin avec les mots importants en gras (RTF : accents échappés).
procedure ShowFinishedText();
var
  Viewer: TRichEditViewer;
begin
  Viewer := TRichEditViewer.Create(WizardForm);
  Viewer.Parent := WizardForm.FinishedLabel.Parent;
  Viewer.Left := WizardForm.FinishedLabel.Left;
  Viewer.Top := WizardForm.FinishedLabel.Top;
  Viewer.Width := WizardForm.FinishedLabel.Width;
  Viewer.Height := WizardForm.FinishedPage.Height - Viewer.Top - ScaleY(8);
  Viewer.BorderStyle := bsNone;
  Viewer.ReadOnly := True;
  Viewer.TabStop := False;
  Viewer.Color := WizardForm.FinishedPage.Color;
  Viewer.UseRichEdit := True;
  Viewer.RTFText :=
    '{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs22 ' +
    '\b Ouvre Revit\b0  : BIMaestro appara\''eet dans son \b onglet du ruban\b0 .\par\par ' +
    'Si Revit demande d''autoriser le compl\''e9ment, choisis \b \''abToujours charger\''bb\b0 .\par ' +
    'Pour d\''e9marrer, essaie les \b \''abParcours guid\''e9s\''bb\b0  !\par\par ' +
    'Clique sur \b Terminer\b0  pour quitter l''assistant.}';
  WizardForm.FinishedLabel.Visible := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpInstalling then
  begin
    if GetArrayLength(MickeyFrames) = 0 then CreateMickey();
    if MickeyTimer = 0 then
      MickeyTimer := SetTimer(0, 0, 100, CreateCallback(@MickeyTick));
  end
  else
    StopMickey();
  if CurPageID = wpFinished then ShowFinishedText();
end;

procedure DeinitializeSetup();
begin
  StopMickey();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  i: Integer;
begin
  Result := True;
  if CurPageID = VersionPage.ID then
  begin
    Result := False;
    for i := 0 to GetArrayLength(PageYears) - 1 do
      if VersionPage.Values[i] then Result := True;
    if not Result then
      MsgBox('Coche au moins une version de Revit.', mbInformation, MB_OK);
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  i: Integer;
begin
  Result := 'BIMaestro {#AppVersion} sera installé pour :';
  for i := 0 to GetArrayLength(PageYears) - 1 do
    if VersionPage.Values[i] then
      Result := Result + NewLine + Space + 'Revit ' + PageYears[i];
end;

// -----------------------------------------------------------------
// Construit le XML du manifest .addin avec un GUID valide
// -----------------------------------------------------------------
function BuildXml(const BinFolder: String): String;
begin
  Result :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<RevitAddIns>' + #13#10 +
    '  <AddIn Type="Application">' + #13#10 +
    '    <Name>BIMaestro</Name>' + #13#10 +
    '    <Assembly>' + BinFolder + '\BIMaestro.dll</Assembly>' + #13#10 +
    '    <AddInId>{E3B0C442-98FC-1C14-9AF7-7D7CE11B9A09}</AddInId>' + #13#10 +
    '    <FullClassName>BIMaestroApp</FullClassName>' + #13#10 +
    '    <VendorId>PAUL LEMERT</VendorId>' + #13#10 +
    '    <VendorDescription>Paul LEMERT</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>';
end;

// -----------------------------------------------------------------
// Après copie, on crée BIMaestro.addin per-user pour chaque Revit présent,
// pointant vers la DLL compilée pour cette version.
// + on dépose un uninstalleur renommé dans Addins\2024
// -----------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
var
  AddinsRoot, BinFolder, Xml: String;
  Versions: array[1..6] of String;
  i: Integer;
  ManifestPath: String;

  DestFolder, SourceExe, SourceDat, DestExe, DestDat: String;
begin
  if CurStep = ssPostInstall then
  begin
    AddinsRoot := ExpandConstant('{userappdata}\Autodesk\Revit\Addins');

    Versions[1] := '2022';
    Versions[2] := '2023';
    Versions[3] := '2024';
    Versions[4] := '2025';
    Versions[5] := '2026';
    Versions[6] := '2027';

    for i := Low(Versions) to High(Versions) do
    begin
      ManifestPath := AddinsRoot + '\' + Versions[i] + '\BIMaestro.addin';
      if not WantsRevit(Versions[i]) then
      begin
        // Pas de manifeste vers une DLL qui n'a pas été installée.
        DeleteFile(ManifestPath);
        Continue;
      end;
      BinFolder := ExpandConstant('{app}\') + PayloadFolder(Versions[i]);
      Xml := BuildXml(BinFolder);
      ForceDirectories(AddinsRoot + '\' + Versions[i]);
      // Le XML declare UTF-8 : encoder aussi les chemins contenant des accents.
      if not SaveStringToFile(ManifestPath, Utf8Encode(Xml), False) then
        RaiseException('Impossible de créer le manifeste : ' + ManifestPath);
    end;

    // --- Copie de l’uninstalleur renommé dans le dossier 2024 ---
    DestFolder := AddinsRoot + '\2024';
    ForceDirectories(DestFolder);

    // Chemins uninstalleur source et .dat
    SourceExe := ExpandConstant('{uninstallexe}');
    SourceDat := ChangeFileExt(SourceExe, '.dat');

    // Cibles renommées
    DestExe := DestFolder + '\Suppression BIMaestro.exe';
    DestDat := DestFolder + '\Suppression BIMaestro.dat';

    if FileExists(SourceExe) then
      FileCopy(SourceExe, DestExe, False);

    if FileExists(SourceDat) then
      FileCopy(SourceDat, DestDat, False);
  end;
end;

[UninstallDelete]
; Supprime le dossier LocalAppData\BIMaestro avec les binaires et sous-dossiers
Type: filesandordirs; Name: "{localappdata}\BIMaestro"
Type: dirifempty;       Name: "{localappdata}\BIMaestro"

; Supprime les manifests BIMaestro.addin per-user
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2022\BIMaestro.addin"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2023\BIMaestro.addin"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2024\BIMaestro.addin"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2025\BIMaestro.addin"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2026\BIMaestro.addin"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2027\BIMaestro.addin"

; Supprime la copie de l’uninstalleur renommé déposée dans 2024
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2024\Suppression BIMaestro.exe"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2024\Suppression BIMaestro.dat"

; Nettoie les dossiers version s’ils sont devenus vides
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2022"
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2023"
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2024"
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2025"
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2026"
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2027"
