; ---------------------------------------------------------------------------
; BIMaestro - installateur Bundle Autodesk/Revit, par utilisateur et sans UAC
;
; Installation :
;   %AppData%\Autodesk\ApplicationPlugins\BIMaestro.bundle
;
; Structure créée :
;   BIMaestro.bundle\
;     PackageContents.xml
;     Contents\
;       BIMaestro.addin
;       BIMaestro.dll
;       BIMaestro.png
;       BIMaestroHelp.html
;       ... dépendances nécessaires
;
; Aucun manifeste "shim" n'est installé dans Revit\Addins : Revit charge
; directement le manifeste du bundle via PackageContents.xml.
; ---------------------------------------------------------------------------

#define AppVersion "1.0.6.2"
#define AddInId "{E3B0C442-98FC-1C14-9AF7-7D7CE11B9A09}"
#define ProductCode "{61D3E380-DC30-49B3-BC8E-B7AD886F29A0}"
#define UpgradeCode "{49998F77-82A0-4D32-BF80-FFA6542040F6}"

[Setup]
AppId={{E3B0C442-98FC-1C14-9AF7-7D7CE11B9A09}
AppName=BIMaestro
AppVersion={#AppVersion}
AppPublisher=Paul Lemert
AppPublisherURL=https://www.bimaestro.fr/
AppSupportURL=https://www.bimaestro.fr/
AppUpdatesURL=https://www.bimaestro.fr/telechargement
LicenseFile=Marketplace\ConditionsUtilisation.txt
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=yes
DefaultDirName={userappdata}\Autodesk\ApplicationPlugins\BIMaestro.bundle
OutputDir=Output
OutputBaseFilename=BIMaestroInstaller-Bundle-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
UninstallDisplayName=BIMaestro
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Files]
; DLL principale et dépendances. Les symboles de débogage ne sont pas distribués.
Source: "bin\Release\*.*"; DestDir: "{app}\Contents"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; \
  Excludes: "*.pdb"

; Ressources publiques du paquet Autodesk.
Source: "Resources\OLD\BIMaestro.png"; DestDir: "{app}\Contents"; \
  DestName: "BIMaestro.png"; Flags: ignoreversion
Source: "Marketplace\BIMaestroHelp.html"; DestDir: "{app}\Contents"; \
  DestName: "BIMaestroHelp.html"; Flags: ignoreversion
Source: "Marketplace\ConditionsUtilisation.txt"; DestDir: "{app}\Contents"; \
  DestName: "ConditionsUtilisation.txt"; Flags: ignoreversion
Source: "Marketplace\PolitiqueConfidentialite.html"; DestDir: "{app}\Contents"; \
  DestName: "PolitiqueConfidentialite.html"; Flags: ignoreversion
Source: "..\LICENSE.txt"; DestDir: "{app}\Contents"; \
  DestName: "LICENSE.txt"; Flags: ignoreversion

[Code]
const
  VENDOR_ID = 'BIMA';
  FULL_CLASS_NAME = 'BIMaestroApp';

function BuildBundleAddinXml(): String;
begin
  Result :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<RevitAddIns>' + #13#10 +
    '  <AddIn Type="Application">' + #13#10 +
    '    <Name>BIMaestro</Name>' + #13#10 +
    '    <Assembly>.\BIMaestro.dll</Assembly>' + #13#10 +
    '    <AddInId>{#AddInId}</AddInId>' + #13#10 +
    '    <FullClassName>' + FULL_CLASS_NAME + '</FullClassName>' + #13#10 +
    '    <VendorId>' + VENDOR_ID + '</VendorId>' + #13#10 +
    '    <VendorDescription>BIMaestro - Paul Lemert</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>';
end;

function BuildPackageContentsXml(): String;
begin
  Result :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="Revit" ProductType="Application"' + #13#10 +
    '  Name="BIMaestro"' + #13#10 +
    '  AppVersion="{#AppVersion}"' + #13#10 +
    '  FriendlyVersion="{#AppVersion}"' + #13#10 +
    '  Description="Suite gratuite d''outils métier pour Autodesk Revit"' + #13#10 +
    '  Author="Paul Lemert"' + #13#10 +
    '  ProductCode="{#ProductCode}"' + #13#10 +
    '  UpgradeCode="{#UpgradeCode}"' + #13#10 +
    '  Icon="./Contents/BIMaestro.png"' + #13#10 +
    '  HelpFile="./Contents/BIMaestroHelp.html"' + #13#10 +
    '  OnlineDocumentation="https://www.bimaestro.fr/"' + #13#10 +
    '  SupportedLocales="Fra"' + #13#10 +
    '  AppNameSpace="com.bimaestro.revit">' + #13#10 +
    '  <CompanyDetails Name="Paul Lemert"' + #13#10 +
    '    Url="https://www.bimaestro.fr/"' + #13#10 +
    '    Email="bimaestro.plugin@gmail.com"' + #13#10 +
    '    Phone="" />' + #13#10 +
    '  <Components Description="BIMaestro pour Revit 2022 à 2027">' + #13#10 +
    '    <RuntimeRequirements OS="Win64" Platform="Revit" SeriesMin="R2022" SeriesMax="R2027" />' + #13#10 +
    '    <ComponentEntry AppName="BIMaestro"' + #13#10 +
    '      Version="{#AppVersion}"' + #13#10 +
    '      ModuleName="./Contents/BIMaestro.addin"' + #13#10 +
    '      AppDescription="Suite d''outils BIMaestro pour Revit" />' + #13#10 +
    '  </Components>' + #13#10 +
    '</ApplicationPackage>';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  PackagePath, AddinPath, DllPath: String;
begin
  if CurStep <> ssPostInstall then
    exit;

  DllPath := ExpandConstant('{app}\Contents\BIMaestro.dll');
  if not FileExists(DllPath) then
  begin
    MsgBox(
      'BIMaestro.dll est introuvable :' + #13#10 + DllPath,
      mbError,
      MB_OK);
    exit;
  end;

  PackagePath := ExpandConstant('{app}\PackageContents.xml');
  AddinPath := ExpandConstant('{app}\Contents\BIMaestro.addin');

  if not SaveStringToFile(PackagePath, BuildPackageContentsXml(), False) then
    RaiseException('Impossible de créer PackageContents.xml.');

  if not SaveStringToFile(AddinPath, BuildBundleAddinXml(), False) then
    RaiseException('Impossible de créer BIMaestro.addin.');
end;

[UninstallDelete]
; L'intégralité du bundle est supprimée. Aucun fichier n'est créé ailleurs.
Type: filesandordirs; Name: "{app}"
