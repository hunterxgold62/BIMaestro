# Native Home feature integration harness

Compiles the actual `BIMaestro/les couleurs/Home/*.cs` feature and its JavaScript resources into a dedicated temporary add-in. No production ribbon or other source is replaced.

```powershell
& ./scripts/home-tests/integration/Build.ps1 -RevitVersion 2025
& ./scripts/home-tests/integration/Start.ps1 -RevitVersion 2025
```

The Start script registers only `BIMaestro.HomeIntegration.addin` for startup, then removes that exact registration. The user accepts any Revit unsigned add-in prompt. It never closes an existing Revit process. A startup timeout still removes the registration; the already launched process can finish starting manually.

The dedicated **BIMaestro Home Test → Couleur et information → Accueil Test** button invokes the actual command and availability class. Preferences are isolated to `tmp/home-tests/integration/YEAR/preferences.json`, so production preferences stay separate. An absent preferences file keeps native Home.

For repeatable integration checks, write valid JSON to `tmp/home-tests/integration/YEAR/control.json`:

```json
{"style":"2025","history":true,"theme":"Dark"}
```

Every key is optional. `style` is `native`, `2023`, `2024`, `2025`, or `2026`; these are appearance choices, independent of host Revit version. `theme` changes the **actual** native theme through `UIThemeManager.CurrentTheme` when that writable API exists. Restore it to the user's original theme after testing. Revit versions without a writable theme API report an error instead of simulating success. Result files are timestamped `result-TICKS.txt`. This control calls the same preference and refresh methods as the command. It does not open model files, execute arbitrary scripts, or suppress security prompts.

The .NET 10 references for 2025/2026 intentionally match the currently installed local Revit patches; use normal project targets when building the distributed plugin. The build script references/copies already restored local NPOI dependencies and Newtonsoft.Json; it downloads nothing.
