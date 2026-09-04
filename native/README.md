# Native record bridge

Publish with .NET 10 Native AOT (Visual Studio Build Tools with MSVC and Windows SDK required):

```text
dotnet publish native/Crer.WinInput.csproj -c Release -r win-x64
```

The DLL exports the `crer_input_*` C ABI required by Deno FFI. It records into a bounded in-memory queue through
Windows low-level input hooks, filtering mouse input by the root HWND of the target CfT process and keyboard input by
its foreground window. It never injects input. The hook is needed as a fallback because Chrome can consume Raw Input
before a separate recorder receives it.

For playback, `crer_input_foreground_process_window` optionally finds a CfT top-level window by its process ID and
uses `SetForegroundWindow`. It is called only when a scenario explicitly sets `browser.window.foreground: true`.
The Deno layer writes those events to NDJSON before normalizing them into scenarios.
