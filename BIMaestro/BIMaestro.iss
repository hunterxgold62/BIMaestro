; --------------------------------------------------------
; BIMaestro.iss — Installateur per‑user sans UAC, .addin 2025+2024
; nettoyage complet à la désinstallation
; --------------------------------------------------------

[Setup]
AppName=BIMaestro
AppVersion=1.0
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=yes
; Ce DefaultDirName n'est utilisé que comme base d'affichage,
; mais nos .addin seront générés en [Code]
DefaultDirName={userappdata}\Autodesk\Revit\Addins\2024
OutputBaseFilename=BIMaestroInstaller
Compression=lzma
SolidCompression=yes

[Files]
; 1) Copie de TOUT votre dossier Release (DLL, XML, PDB, sous‑dossiers…)
Source: "C:\Users\plemert\OneDrive - SAS H.C.M. HOLDING CABINET MERLIN\Documents\BIMaestro\BIMaestro\bin\Release\*.*"; \
  DestDir: "{localappdata}\BIMaestro\Bin"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Pas de raccourcis pour un plugin Revit

[Code]
// -----------------------------------------------------------------
// Construit le XML du manifest .addin
// -----------------------------------------------------------------
function BuildXml(const BinFolder: String): String;
begin
  Result :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<RevitAddIns>' + #13#10 +
    '  <AddIn Type="Application">' + #13#10 +
    '    <Name>BIMaestro</Name>' + #13#10 +
    '    <Assembly>' + BinFolder + '\BIMaestro.dll</Assembly>' + #13#10 +
    '    <AddInId>91a60dd2-8c82-4c78-b9fe-36f97bfbd19b</AddInId>' + #13#10 +
    '    <FullClassName>App</FullClassName>' + #13#10 +
    '    <VendorId>PAUL LEMERT</VendorId>' + #13#10 +
    '    <VendorDescription>Paul LEMERT</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>';
end;

// -----------------------------------------------------------------
// Après copie, on crée les manifests pour Revit 2025 et 2024
// -----------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
var
  AddinsRoot, BinFolder, Xml: String;
  Versions: array[1..2] of String;
  i: Integer;
  ManifestPath: String;
begin
  if CurStep = ssPostInstall then
  begin
    // 1) Où sont copiées les DLL
    BinFolder := ExpandConstant('{localappdata}\BIMaestro\Bin');

    // 2) Racine des AddIns Revit sous Roaming
    AddinsRoot := ExpandConstant('{userappdata}\Autodesk\Revit\Addins');

    // 3) Versions ciblées
    Versions[1] := '2025';
    Versions[2] := '2024';

    // 4) Génère le XML une seule fois
    Xml := BuildXml(BinFolder);

    // 5) Pour chaque version : créer dossier + écrire BIMaestro.addin
    for i := Low(Versions) to High(Versions) do
    begin
      // Crée le dossier si besoin
      ForceDirectories(AddinsRoot + '\' + Versions[i]);
      // Chemin complet du manifest
      ManifestPath := AddinsRoot + '\' + Versions[i] + '\BIMaestro.addin';
      SaveStringToFile(ManifestPath, Xml, False);
    end;
  end;
end;

[UninstallDelete]
; Supprime complètement le dossier LocalAppData\BIMaestro (Bin et sous‑dossiers)
Type: filesandordirs; Name: "{localappdata}\BIMaestro"
; (Optionnel) Si vous voulez également ôter le dossier parent s'il est vide
Type: dirifempty;       Name: "{localappdata}\BIMaestro"

; — Supprime les manifests dans 2023 & 2024
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2023\BIMaestro.addin"
Type: files;            Name: "{userappdata}\Autodesk\Revit\Addins\2024\BIMaestro.addin"
; — Nettoie les dossiers versions s'ils sont devenus vides
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2023"
Type: dirifempty;       Name: "{userappdata}\Autodesk\Revit\Addins\2024"
