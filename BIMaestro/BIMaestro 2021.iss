; --------------------------------------------------------
; BIMaestro.iss — Installateur per‑user sans UAC, .addin 2021
; Nettoyage complet à la désinstallation
; --------------------------------------------------------

[Setup]
; Nom et version de ton plugin
AppName=BIMaestro
AppVersion=1.0

; Installation per‑user, sans UAC
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=yes

; Dossier où sera généré le .addin pour Revit 2021
DefaultDirName={userappdata}\Autodesk\Revit\Addins\2021

; Nom de l’installateur généré
OutputBaseFilename=BIMaestroInstaller
Compression=lzma
SolidCompression=yes

[Files]
; Copie **tous** tes fichiers de build (DLL, PDB, XML, dossiers de langues…)
Source: "C:\Users\plemert\OneDrive - SAS H.C.M. HOLDING CABINET MERLIN\Documents\BIMaestro\BIMaestro\bin\Release\*.*"; \
    DestDir: "{localappdata}\BIMaestro\Bin"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Aucun raccourci nécessaire pour un plugin Revit

[Code]
// -----------------------------------------------------------------
// Construit le contenu du manifest .addin
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
// Après copie, crée le manifest pour Revit 2021
// -----------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
var
  AddinsRoot, Dir2021, BinFolder, Xml: String;
begin
  if CurStep = ssPostInstall then
  begin
    // 1) Le dossier où sont copiées les DLL
    BinFolder := ExpandConstant('{localappdata}\BIMaestro\Bin');

    // 2) Racine des AddIns Revit en Roaming
    AddinsRoot := ExpandConstant('{userappdata}\Autodesk\Revit\Addins');

    // 3) Création du dossier 2021
    Dir2021 := AddinsRoot + '\2021';
    ForceDirectories(Dir2021);

    // 4) Génération du XML
    Xml := BuildXml(BinFolder);

    // 5) Sauvegarde du manifest .addin
    SaveStringToFile(Dir2021 + '\BIMaestro.addin', Xml, False);
  end;
end;

[UninstallDelete]
; Supprime complètement le dossier Bin et son contenu
Type: filesandordirs; Name: "{localappdata}\BIMaestro\Bin"
; Supprime le dossier BIMaestro si vide
Type: dirifempty;       Name: "{localappdata}\BIMaestro"
