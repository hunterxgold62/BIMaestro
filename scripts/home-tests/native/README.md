# Native Home probe

Temporary standalone Revit application. Does not invoke document/model APIs or modify production BIMaestro. The app discovers browser controls through WPF trees and Home assembly static fields, then evaluates a page URL guard before any supplied code executes. Project Browser and other page URLs are rejected.

Build: `./scripts/home-tests/native/Build.ps1 -RevitVersion 2025` (2023–2026).

Launch (requires permission to write user addin directory and start Revit): `./scripts/home-tests/native/Start.ps1 -RevitVersion 2025`.

The version output directory is `tmp/home-tests/2025`. Watch `started.txt`, `probe.log`, and `result-*.txt`. After startup is confirmed, remove ONLY the temporary manifest `BIMaestro.HomeProbe.addin` from the corresponding user addin directory. Other addins are unaffected.

Without a control file the probe returns JSON containing title, URL, and complete HTML. To apply a script without restarting, write `control.js` in the version output folder. It is a JavaScript function body, so include `return` to receive a result. It reloads on modification within approximately three seconds of Revit becoming idle. The page URL must contain ModelBrowser, ApplicationHome, or RevitHome; actual preset code should also validate expected DOM selectors before mutating.

The probe retries discovery and logs periodically while Revit remains open. It writes no installation files itself. Native shutdown unsubscribes Idling. Session-only injected CSS disappears when the Home page reloads or Revit closes.

2023/2024 compile against .NET Framework 4.8 reference assemblies. The installed 2025.5/2026 APIs on this computer reference .NET 10; their builds use the installed 10.0.10 managed runtime assemblies. This is machine-specific, not a claim about baseline Autodesk version requirements.
