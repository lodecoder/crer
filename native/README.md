# Native record bridge

Publish with .NET 10 Native AOT (Visual Studio Build Tools with MSVC and Windows SDK required):

```text
dotnet publish native/Crer.WinInput.csproj -c Release -r win-x64
```

The DLL exports the `crer_input_*` C ABI required by Deno FFI. It records into a bounded in-memory queue through
Windows low-level input hooks, filtering mouse input by the root HWND of the target CfT process and keyboard input by
its foreground window. It never injects input. The hook is needed as a fallback because Chrome can consume Raw Input
before a separate recorder receives it.

For playback, `crer_input_set_process_topmost_only` finds a CfT top-level window by its process ID and changes only
its topmost state with `SetWindowPos`. `crer_input_foreground_process_window` separately requests foreground focus.
This separation lets the Deno watchdog repair a lost topmost state without repeatedly stealing focus. The legacy
`crer_input_set_process_topmost` export retains the combined behavior for ABI compatibility. These APIs are used only
when a scenario explicitly sets `browser.window.foreground: true`; playback clears the topmost flag before closing CfT.
Windows may reject foreground focus even after a thread-input retry; that result is diagnostic only when the topmost
operation itself succeeded.
The Deno layer writes those events to NDJSON before normalizing them into scenarios.
