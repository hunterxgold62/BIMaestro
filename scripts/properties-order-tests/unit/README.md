# Deterministic layout regression tests

Run `./scripts/properties-order-tests/unit/Run.ps1` from PowerShell. The script compiles the current production `PropertiesPaletteLayout.cs`, `AppearanceOrdering.cs` and `PropertyPreviewRow.cs` directly, using the installed Roslyn compiler and .NET 10 runtime. It performs no network access or package restoration. Outputs go to `tmp/properties-order-tests/unit`.

The native grid, context and preference containers are test doubles. The production permutation, verification, rollback and reconstruction logic runs unchanged, with `BIMAESTRO_PROPERTIES_TESTS` enabling the same experimental code used by the Revit probe.

The eleven scenarios cover combined group/parameter movements, the global-parameter marker, value preservation, reset, semantic mapping failure and rollback, missing identities before and after a partial movement, active editing, native reconstruction and grid replacement, changing collapse markers, empty identity coverage, transient capture and recovery without losing the previous baseline, unexpected native contents, and version admission.

These tests verify control flow and state transitions. The 2023–2027 case tests the layout's version guard only. They do not validate the native ABI, the real Revit editors, keyboard/mouse routing, or compatibility with any Revit version; those require the separate live Revit probe.

The script also builds a second executable **without** `BIMAESTRO_PROPERTIES_TESTS`. Its three scenarios execute the production admission rules through `Apply`: walls in 2023–2027 can move and restore, unqualified contexts cannot move, and an already ordered wall cannot bypass qualification after switching categories with a global profile. The `PRODUCTION_QUALIFICATION_TESTS` symbol selects only the runner's cases and does not alter production sources.
