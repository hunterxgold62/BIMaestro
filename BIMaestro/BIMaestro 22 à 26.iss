; --------------------------------------------------------
; BIMaestro.iss — Installateur per-user sans UAC
; installe BIMaestro.addin en local (2022–2026)
; + Copie d’un uninstalleur renommé "Suppression BIMaestro.exe"
;   dans le dossier Addins\2024
; --------------------------------------------------------

[Setup]
AppId={{E3B0C442-98FC-1C14-9AF7-7D7CE11B9A09}}
AppName=BIMaestro
AppVersion=1.0.5.9
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=yes
DefaultDirName={localappdata}\BIMaestro\Bin
OutputBaseFilename=BIMaestroInstaller
Compression=lzma
SolidCompression=yes

[Files]
; Copie de tout le dossier Release de votre build vers %LocalAppData%\BIMaestro\Bin
Source: "bin\Release\*.*"; \
  DestDir: "{localappdata}\BIMaestro\Bin"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
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
    '    <FullClassName>App</FullClassName>' + #13#10 +
    '    <VendorId>PAUL LEMERT</VendorId>' + #13#10 +
    '    <VendorDescription>Paul LEMERT</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>';
end;

// -----------------------------------------------------------------
// Après copie, on crée BIMaestro.addin per-user pour Revit 2022–2025
// + on dépose un uninstalleur renommé dans Addins\2024
// -----------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
var
  AddinsRoot, BinFolder, Xml: String;
  Versions: array[1..5] of String;
  i: Integer;
  ManifestPath: String;

  DestFolder, SourceExe, SourceDat, DestExe, DestDat: String;
begin
  if CurStep = ssPostInstall then
  begin
    BinFolder := ExpandConstant('{localappdata}\BIMaestro\Bin');
    AddinsRoot := ExpandConstant('{userappdata}\Autodesk\Revit\Addins');

    Versions[1] := '2022';
    Versions[2] := '2023';
    Versions[3] := '2024';
    Versions[4] := '2025';
      Versions[5] := '2026';

    Xml := BuildXml(BinFolder);

    for i := Low(Versions) to High(Versions) do
    begin
      ForceDirectories(AddinsRoot + '\' + Versions[i]);
      ManifestPath := AddinsRoot + '\' + Versions[i] + '\BIMaestro.addin';
      SaveStringToFile(ManifestPath, Xml, False);
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
      FileCopy(SourceExe, DestExe, False);  // False = écrase si existe
    if FileExists(SourceDat) then
      FileCopy(SourceDat, DestDat, False);
  end;
end;

[UninstallDelete]
; Supprime le dossier LocalAppData\BIMaestro (binaires et sous-dossiers)
Type: filesandordirs; Name: "{localappdata}\BIMaestro"
Type: dirifempty;       Name: "{localappdata}\BIMaestro"

; Supprime les manifests BIMaestro.addin per-user
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2022\BIMaestro.addin"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2023\BIMaestro.addin"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2024\BIMaestro.addin"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2025\BIMaestro.addin"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2026\BIMaestro.addin"


; Supprime la copie de l’uninstalleur renommé déposée dans 2024 (si ce n’est pas elle qui s’auto-supprime)
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2024\Suppression BIMaestro.exe"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2024\Suppression BIMaestro.dat"

; Nettoie les dossiers version s’ils sont devenus vides
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2022"
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2023"
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2024"
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2025"
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2026"

