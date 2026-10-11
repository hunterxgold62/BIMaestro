; Included by both installers. Never register newer Revit hosts with the
; .NET Framework DLL. A full 2023-2027 package requires all versioned builds.
#if !FileExists("bin\Release2024\BIMaestro.dll")
  #error Compiler la configuration Release2024 avant de creer l'installateur.
#endif
#if !FileExists("bin\Revit2025\Release\BIMaestro.dll")
  #error Compiler BIMaestro.Revit2025.csproj avec RevitVersion=2025.
#endif
#if !FileExists("bin\Revit2026\Release\BIMaestro.dll")
  #error Compiler BIMaestro.Revit2025.csproj avec RevitVersion=2026.
#endif
#if !FileExists("bin\Revit2027\Release\BIMaestro.dll")
  #error Revit 2027 non compile : fournir ses API et compiler RevitVersion=2027 avant de distribuer un installateur 2023-2027.
#endif
Source: "bin\Release2024\*.*"; DestDir: "{#RevitPayloadRoot}\Revit2024"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,BIMaestro - Copie.dll,BIMaestro.Updater.*"
Source: "bin\Revit2025\Release\*.*"; DestDir: "{#RevitPayloadRoot}\Revit2025"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,BIMaestro - Copie.dll,BIMaestro.Updater.*"
Source: "bin\Revit2026\Release\*.*"; DestDir: "{#RevitPayloadRoot}\Revit2026"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,BIMaestro - Copie.dll,BIMaestro.Updater.*"
Source: "bin\Revit2027\Release\*.*"; DestDir: "{#RevitPayloadRoot}\Revit2027"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,BIMaestro - Copie.dll,BIMaestro.Updater.*"
