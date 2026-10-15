# crer for Windows x64

[日本語](user-guide.ja.md)

Once the Windows x64 assets are available on GitHub Releases, use the steps below.

Use Windows x64 with Windows PowerShell 5.1 or PowerShell 7. Administrator access, .NET, Deno, Node.js, and Bun are not required. Chrome for Testing (CfT) is installed separately.

## Install

Download `crer-win-x64.zip` and `SHA256SUMS` from [GitHub Releases](https://github.com/lodecoder/crer/releases) into the same folder. In PowerShell, open that folder and compare the ZIP's hash with its entry in `SHA256SUMS` (letter case does not matter):

```powershell
Get-FileHash .\crer-win-x64.zip -Algorithm SHA256
Get-Content .\SHA256SUMS
```

Continue only if the hashes match:

```powershell
Unblock-File .\crer-win-x64.zip
Expand-Archive .\crer-win-x64.zip -DestinationPath .\crer-setup
& .\crer-setup\install.ps1
```

The installer uses `%LOCALAPPDATA%\Programs\crer` and adds it to your user PATH. Open a new PowerShell, then run `crer help`.

If PowerShell reports that running scripts is disabled, run `Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned` in the same PowerShell and retry. This setting ends when you close that PowerShell.

Optional installer arguments are `-InstallDirectory "$env:LOCALAPPDATA\Programs\crer-custom"` and `-NoPath`. With `-NoPath`, invoke `crer.exe` by its full path. Replace the default installation path below if you customized it.

## Configure Chrome for Testing

Install CfT with the bundled helper:

```powershell
& "$env:LOCALAPPDATA\Programs\crer\install-chrome-for-testing.ps1" -InstallRoot "$env:LOCALAPPDATA\crer\browsers" -Version stable
```

The helper registers `CRER_CHROME` and `CRER_CHROME_MANIFEST` for your user. Open a new PowerShell and run:

```powershell
crer doctor
```

Check that Chrome exists and is recognized as CfT, and `ffiExists` is `true`. Keep `crer.exe` and `crer-win-input.dll` together. crer uses its own CfT window and profile; it does not use your regular Chrome.

To register an existing **CfT** installation instead of downloading one, use the same helper with `-ChromePath 'C:\path\to\chrome.exe'` and `-InstallRoot "$env:LOCALAPPDATA\crer\browsers"`. This updates the manifest and both environment variables together. `--chrome` overrides `CRER_CHROME` for one command; setting that variable alone does not update the manifest. Dot-sourcing the helper is also available when applying its configuration in the current PowerShell.

## First playback

Work outside the installation folder:

```powershell
$work = Join-Path $env:USERPROFILE 'crer-work'
New-Item -ItemType Directory -Force $work | Out-Null
Set-Location $work
```

Save this as `first.crer.yaml`:

```yaml
version: 1
name: first-run
browser:
  initial_url: https://example.com/
steps:
  - { do: wait_for, state: complete }
  - { do: screenshot, name: first-page }
```

```powershell
crer validate .\first.crer.yaml
crer play .\first.crer.yaml
```

The file goes immediately after the command; option values are separate arguments. `run <plan-file>` executes a plan.

## Record and replay

Choose a new raw filename for each recording: an existing raw file is appended to. Replace the example URL in both commands with the page you want to record.

```powershell
crer record .\recording-01.ndjson --url https://example.com/
```

1. Click the magenta square at the page's upper-left corner and wait for `Calibration complete`.
2. Perform your actions in the dedicated CfT window.
3. Return to the PowerShell that started recording and press Enter to stop. Keep the raw file and its `.ndjson.meta.json` sidecar together.

```powershell
crer normalize .\recording-01.ndjson --url https://example.com/ --output .\recorded.crer.yaml
crer validate .\recorded.crer.yaml
crer play .\recorded.crer.yaml
```

Review the generated YAML before replay. IME committed text and complex shortcuts may need manual edits. Playback sends CDP input to CfT rather than moving the physical cursor or typing into other apps.

Ctrl+C or closing the dedicated CfT also stops recording. `--stop-file <path>` stops when that file exists; start with a nonexistent path. `--duration-ms 30000` provides timed recording without marker calibration. Enter stopping applies only to an interactive terminal when neither stop option is supplied.

## Files, updates, and removal

Logs, diagnostics, and screenshots are under the working folder's `.crer\runs\<run-id>`; playback prints `runDir`. Inspect it with `crer inspect '<runDir>'`. `--keep-artifacts` preserves the temporary browser profile; logs and screenshots are retained without it too. Persistent profiles must be inside that working folder's `.crer\profiles` and cannot be shared concurrently. Scenarios using `browser.window.foreground: true` change focus and window order. Display mismatch diagnostics should be reviewed before changing viewport settings.

To update, close crer and its dedicated CfT, then verify and extract the new ZIP into a fresh setup folder and run its installer with the same installation options.

Before removal, back up your YAML, raw recordings, sidecars, and work folders. Then run:

```powershell
& "$env:LOCALAPPDATA\Programs\crer\uninstall.ps1"
```

The uninstaller removes tracked installation files and the PATH entry added by the installer. It keeps CfT, Chrome environment variables, work folders including `.crer` profiles/artifacts, and installation files you modified.
