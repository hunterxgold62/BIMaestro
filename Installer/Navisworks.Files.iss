; Included from the [Files] section of both existing BIMaestro installers.
; Build both versions with Installer\Build-Navisworks.ps1 before compiling Inno Setup.
; Explicit files: Autodesk API assemblies must never be redistributed.
Source: "..\Installer\Navisworks.PackageContents.xml"; DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BIMaestro.Navisworks.bundle"; DestName: "PackageContents.xml"; Flags: ignoreversion
Source: "..\BIMaestro.Navisworks\bin\Release\2025\BIMaestro.Navisworks.dll"; DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BIMaestro.Navisworks.bundle\Contents\2025"; Flags: ignoreversion
Source: "..\BIMaestro.Navisworks\bin\Release\2025\fr-FR\BIMaestroRibbon.xaml"; DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BIMaestro.Navisworks.bundle\Contents\2025\fr-FR"; Flags: ignoreversion
Source: "..\BIMaestro.Navisworks\bin\Release\2025\en-US\BIMaestroRibbon.xaml"; DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BIMaestro.Navisworks.bundle\Contents\2025\en-US"; Flags: ignoreversion
Source: "..\BIMaestro.Navisworks\bin\Release\2027\BIMaestro.Navisworks.dll"; DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BIMaestro.Navisworks.bundle\Contents\2027"; Flags: ignoreversion
Source: "..\BIMaestro.Navisworks\bin\Release\2027\fr-FR\BIMaestroRibbon.xaml"; DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BIMaestro.Navisworks.bundle\Contents\2027\fr-FR"; Flags: ignoreversion
Source: "..\BIMaestro.Navisworks\bin\Release\2027\en-US\BIMaestroRibbon.xaml"; DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BIMaestro.Navisworks.bundle\Contents\2027\en-US"; Flags: ignoreversion
