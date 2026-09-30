# Claude PDF transport validation — 2026-09-28

The reported symptom is an unqualified Windows broken-pipe message whenever a colleague sends a PDF. The exact failure on that colleague's machine has not been reproduced locally. The first incompatible stream-input/JSON-output combination was reproduced with the official Claude Code 2.1.283 executable; the corrected combination is accepted.

The `PDF-2` transport now uses a dedicated UTF-8 encoder without a BOM, sends bounded chunks, closes the underlying stdin handle, drains stdout/stderr, and prevents cleanup exceptions from replacing the original error. Failed uploads have a bounded exit wait and can be cancelled. A failing streamed result is extracted rather than dumping all CLI events into the conversation. No automatic replay of a potentially executed request was added.

`%LOCALAPPDATA%\BIMaestro\Claude\last-transport.json` contains the transport revision, loaded assembly module ID, runtime, executable version, stage, exit code, lengths and exception type/HRESULT. It excludes prompts, image/PDF data, tokens and raw stdout/stderr. Successful authentication checks do not overwrite the last send diagnostic. MEP mode uses its existing `Claude\MepAssistant` directory.

## Reproduce

Run `Run.ps1` in PowerShell 7 for the local protocol and UI tests. It includes early CLI exit during a large upload, a stalled stdin cancelled by the user, streamed API errors, process cleanup, and discussion resume.

Run `TestClaudeNativeTransport.ps1 -ClaudeExecutable <absolute path to official claude.exe>` in PowerShell 7 for the native transport integration test. The harness builds its own isolated executable, runs it on both .NET Framework and .NET 8, isolates Claude configuration, clears inherited Claude/Anthropic credentials, and points the CLI at a synthetic HTTP API listening only on loopback. It supplies a dummy token solely for that local API; it never logs into a real account or requests model inference. A loopback listener may require permission outside a restricted sandbox.

Validated with Claude Code **2.1.283**:

- Three valid 650×650 PNG pages, totaling **5,073,318 base64 characters**, reach the real CLI. The native CLI recompresses them to JPEG; the API fixture verifies the image count, MIME type and dimensions.
- Accented text and an emoji reach the API without UTF-8 replacement characters.
- Structured output is returned and the native session resumes for a text follow-up.
- An intentional API image rejection reaches the UI as a concise provider error, without initialisation-event dumps.
- Both runtime variants pass. Release and Revit 2025 builds pass.

The test does **not** establish that the colleague's Claude account or exact CLI version accepts their particular PDF. Retest the uniquely named `BIMaestroInstaller-Claude-PDF2.exe` after fully exiting Revit. If it fails, obtain the complete error and the local diagnostic. The build manifest next to the installer records its SHA-256 and source DLL module ID so a stale loaded assembly can be identified.
