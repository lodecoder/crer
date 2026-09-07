# Native record bridge

Publish with .NET 10 Native AOT (Visual Studio Build Tools with MSVC and Windows SDK required):

```text
dotnet publish native/Crer.WinInput.csproj -c Release -r win-x64
```

Run the managed native-state fault tests with `dotnet run --project native/tests/Crer.WinInput.NativeTests.csproj`.

The DLL exports the `crer_input_*` C ABI required by Deno FFI. It records into a bounded in-memory queue through
Windows low-level input hooks, filtering mouse input by the root HWND of the target CfT process and keyboard input by
its foreground window. It never injects input. The hook is needed as a fallback because Chrome can consume Raw Input
before a separate recorder receives it.

`crer_input_is_running` lets the polling layer detect hook failure or queue overflow immediately. `start` clears the
queue and thread state before each recording, while `stop` waits for the native thread and returns `ERROR_TIMEOUT` if
it cannot terminate within five seconds. The Deno layer preserves partial NDJSON and marks its metadata `incomplete`
when the recorder reports a terminal error.

For playback, `crer_input_set_process_topmost_only` finds a CfT top-level window by its process ID and changes only
its topmost state with `SetWindowPos`. `crer_input_foreground_process_window` separately requests foreground focus.
This separation lets the Deno watchdog repair a lost topmost state without repeatedly stealing focus. The legacy
`crer_input_set_process_topmost` export retains the combined behavior for ABI compatibility. These APIs are used only
when a scenario explicitly sets `browser.window.foreground: true`; playback clears the topmost flag before closing CfT.
Windows may reject foreground focus even after a thread-input retry; that result is diagnostic only when the topmost
operation itself succeeded.
The Deno layer writes those events to NDJSON before normalizing them into scenarios.
