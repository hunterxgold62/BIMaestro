; --------------------------------------------------------
; BIMaestro_bundle_plus_shim.iss — per-user sans UAC
; - Installe un bundle Autoloader :
;     %AppData%\Autodesk\ApplicationPlugins\BIMaestro.bundle
; - Génère PackageContents.xml + Contents\BIMaestro.addin
; - Ajoute un "shim" .addin dans :
;     %AppData%\Autodesk\Revit\Addins\2023..2026
;   qui pointe vers la DLL du bundle (fallback si l’autoloader est ignoré)
; --------------------------------------------------------

[Setup]
AppId={{E3B0C442-98FC-1C14-9AF7-7D7CE11B9A09}}
AppName=BIMaestro
AppVersion=1.0.5.9
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=yes
DefaultDirName={userappdata}\Autodesk\ApplicationPlugins\BIMaestro.bundle
OutputBaseFilename=BIMaestroInstaller
Compression=lzma
SolidCompression=yes
UninstallDisplayName=BIMaestro
ArchitecturesInstallIn64BitMode=x64

[Files]
; Copie ton build (DLL + dépendances + assets) dans Contents\
Source: "bin\Release\*.*"; DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BIMaestro.bundle\Contents"; Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
const
  VENDOR_ID = 'BIMA';
  // Mets ici le nom EXACT de ta classe IExternalApplication (avec namespace si besoin)
  // Exemple: 'BIMaestro.Entry.BIMaestroApp'
  FULL_CLASS_NAME = 'BIMaestroApp';

function GetAppVersion(): String;
begin
  Result := ExpandConstant('{#SetupSetting("AppVersion")}');
end;

function BundleRoot(): String;
begin
  Result := ExpandConstant('{userappdata}\Autodesk\ApplicationPlugins\BIMaestro.bundle');
end;

function ContentsDir(): String;
begin
  Result := BundleRoot() + '\Contents';
end;

function BundleDllPath(): String;
begin
  Result := ContentsDir() + '\BIMaestro.dll';
end;

function RevitAddinsRoot(): String;
begin
  Result := ExpandConstant('{userappdata}\Autodesk\Revit\Addins');
end;

// .addin interne au bundle (chemin relatif, propre)
function BuildBundleAddinXml(): String;
begin
  Result :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<RevitAddIns>' + #13#10 +
    '  <AddIn Type="Application">' + #13#10 +
    '    <Name>BIMaestro</Name>' + #13#10 +
    '    <Assembly>.\BIMaestro.dll</Assembly>' + #13#10 +
    '    <AddInId>{E3B0C442-98FC-1C14-9AF7-7D7CE11B9A09}</AddInId>' + #13#10 +
    '    <FullClassName>' + FULL_CLASS_NAME + '</FullClassName>' + #13#10 +
    '    <VendorId>' + VENDOR_ID + '</VendorId>' + #13#10 +
    '    <VendorDescription>BIMaestro - Paul LEMERT</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>';
end;

// .addin shim dans Revit\Addins\20xx (chemin absolu vers la DLL du bundle)
function BuildShimAddinXml(const AbsoluteDllPath: String): String;
begin
  Result :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<RevitAddIns>' + #13#10 +
    '  <AddIn Type="Application">' + #13#10 +
    '    <Name>BIMaestro</Name>' + #13#10 +
    '    <Assembly>' + AbsoluteDllPath + '</Assembly>' + #13#10 +
    '    <AddInId>{E3B0C442-98FC-1C14-9AF7-7D7CE11B9A09}</AddInId>' + #13#10 +
    '    <FullClassName>' + FULL_CLASS_NAME + '</FullClassName>' + #13#10 +
    '    <VendorId>' + VENDOR_ID + '</VendorId>' + #13#10 +
    '    <VendorDescription>BIMaestro - Paul LEMERT</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>';
end;

// PackageContents.xml (Autoloader)
function BuildPackageContentsXml(const AppVer: String): String;
begin
  Result :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="Revit" ProductType="Application" ' +
    'Name="BIMaestro" AppVersion="' + AppVer + '" Description="BIMaestro for Revit" ' +
    'Author="Paul LEMERT" FriendlyVersion="' + AppVer + '" SupportedLocales="Enu|Fra" ' +
    'AppNameSpace="appstore.exchange.autodesk.com">' + #13#10 +
    '  <CompanyDetails Name="Paul LEMERT" Url="" Email="" Phone="" />' + #13#10 +

    '  <Components Description="Revit 2023">' + #13#10 +
    '    <RuntimeRequirements OS="Win64" Platform="Revit" SeriesMin="R2023" SeriesMax="R2023" />' + #13#10 +
    '    <ComponentEntry AppName="BIMaestro" Version="' + AppVer + '" ModuleName="./Contents/BIMaestro.addin" AppDescription="BIMaestro" />' + #13#10 +
    '  </Components>' + #13#10 +

    '  <Components Description="Revit 2024">' + #13#10 +
    '    <RuntimeRequirements OS="Win64" Platform="Revit" SeriesMin="R2024" SeriesMax="R2024" />' + #13#10 +
    '    <ComponentEntry AppName="BIMaestro" Version="' + AppVer + '" ModuleName="./Contents/BIMaestro.addin" AppDescription="BIMaestro" />' + #13#10 +
    '  </Components>' + #13#10 +

    '  <Components Description="Revit 2025">' + #13#10 +
    '    <RuntimeRequirements OS="Win64" Platform="Revit" SeriesMin="R2025" SeriesMax="R2025" />' + #13#10 +
    '    <ComponentEntry AppName="BIMaestro" Version="' + AppVer + '" ModuleName="./Contents/BIMaestro.addin" AppDescription="BIMaestro" />' + #13#10 +
    '  </Components>' + #13#10 +

    '  <Components Description="Revit 2026">' + #13#10 +
    '    <RuntimeRequirements OS="Win64" Platform="Revit" SeriesMin="R2026" SeriesMax="R2026" />' + #13#10 +
    '    <ComponentEntry AppName="BIMaestro" Version="' + AppVer + '" ModuleName="./Contents/BIMaestro.addin" AppDescription="BIMaestro" />' + #13#10 +
    '  </Components>' + #13#10 +

    '</ApplicationPackage>';
end;

procedure WriteShimForYear(const Year: String; const AbsoluteDllPath: String);
var
  Dir, Path: String;
begin
  Dir := RevitAddinsRoot() + '\' + Year;
  ForceDirectories(Dir);
  Path := Dir + '\BIMaestro.addin';
  SaveStringToFile(Path, BuildShimAddinXml(AbsoluteDllPath), False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  PcPath, BundleAddinPath, DllPath, PcXml, AddinXml: String;
begin
  if CurStep = ssPostInstall then
  begin
    ForceDirectories(BundleRoot());
    ForceDirectories(ContentsDir());

    // 1) PackageContents.xml
    PcPath := BundleRoot() + '\PackageContents.xml';
    PcXml := BuildPackageContentsXml(GetAppVersion());
    SaveStringToFile(PcPath, PcXml, False);

    // 2) BIMaestro.addin dans le bundle
    BundleAddinPath := ContentsDir() + '\BIMaestro.addin';
    AddinXml := BuildBundleAddinXml();
    SaveStringToFile(BundleAddinPath, AddinXml, False);

    // 3) Shims .addin pour Revit 2023..2026
    DllPath := BundleDllPath();
    if not FileExists(DllPath) then
    begin
      MsgBox('BIMaestro.dll introuvable : ' + DllPath + #13#10 +
             'Vérifie le Source: "bin\Release\*.*" dans [Files].',
             mbError, MB_OK);
      exit;
    end;

    WriteShimForYear('2023', DllPath);
    WriteShimForYear('2024', DllPath);
    WriteShimForYear('2025', DllPath);
    WriteShimForYear('2026', DllPath);

    MsgBox('Installation terminée.' + #13#10 +
           'Ferme et relance Revit.' + #13#10 +
           'Bundle: ' + BundleRoot() + #13#10 +
           'Shims: ' + RevitAddinsRoot(),
           mbInformation, MB_OK);
  end;
end;

[UninstallDelete]
; Supprime le bundle
Type: filesandordirs; Name: "{userappdata}\Autodesk\ApplicationPlugins\BIMaestro.bundle"

; Supprime les shims .addin
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2023\BIMaestro.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\BIMaestro.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\BIMaestro.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\BIMaestro.addin"

; Nettoie si vide
Type: dirifempty; Name: "{userappdata}\Autodesk\Revit\Addins\2023"
Type: dirifempty; Name: "{userappdata}\Autodesk\Revit\Addins\2024"
Type: dirifempty; Name: "{userappdata}\Autodesk\Revit\Addins\2025"
Type: dirifempty; Name: "{userappdata}\Autodesk\Revit\Addins\2026"
Type: dirifempty; Name: "{userappdata}\Autodesk\Revit\Addins"
Type: dirifempty; Name: "{userappdata}\Autodesk\Revit"
Type: dirifempty; Name: "{userappdata}\Autodesk\ApplicationPlugins"
Type: dirifempty; Name: "{userappdata}\Autodesk"
