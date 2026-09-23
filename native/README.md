# Native record bridge

Publish with .NET 10 Native AOT (Visual Studio Build Tools with MSVC and Windows SDK required):

```text
dotnet publish native/Crer.WinInput.csproj -c Release -r win-x64
```

Run the managed native-state fault tests with `dotnet run --project native/tests/Crer.WinInput.NativeTests.csproj`.

The DLL exports version 2 of the `crer_input_*` C ABI required by Deno FFI. Version 2 makes mouse events and content
rectangles explicitly physical-pixel coordinates. It records into a bounded in-memory queue through
Windows Raw Input (`RIDEV_INPUTSINK`) for the mouse and a low-level hook for the keyboard, filtering mouse input by
the root HWND of the target CfT process and keyboard input by its foreground window. It never injects input.
The message loop dispatches `WM_INPUT`; only Raw Input produces mouse events, avoiding duplicate clicks.

Mouse coordinates are sampled with `GetPhysicalCursorPos` when processing each Raw Input packet. The recording thread
and content-window measurement use Per-Monitor-V2 DPI awareness so the event point and HWND rectangle remain in
the same physical-pixel coordinate space at Windows display scales such as 125%, 150%, or 200%.

`crer_input_is_running` lets the polling layer detect hook failure or queue overflow immediately. `start` clears the
queue and thread state before each recording, while `stop` waits for the native thread and returns `ERROR_TIMEOUT` if
it cannot terminate within five seconds. The Deno layer preserves partial NDJSON and marks its metadata `incomplete`
when the recorder reports a terminal error.

For playback, `crer_input_set_process_topmost_only` finds a visible, unowned CfT top-level window by its process ID.
Hidden widgets and owned menus/tooltips are excluded so success cannot refer only to a helper window.
It changes the topmost state with `SetWindowPos(SWP_NOACTIVATE)` and verifies `WS_EX_TOPMOST` afterward.
`crer_input_foreground_process_window` separately requests foreground focus.
This separation lets the Deno watchdog repair a lost topmost state without repeatedly stealing focus. The legacy
`crer_input_set_process_topmost` export retains the combined behavior for ABI compatibility. These APIs are used only
when a scenario explicitly sets `browser.window.foreground: true`; playback clears the topmost flag before closing CfT.
Windows may reject foreground focus even after a thread-input retry; that result is diagnostic only when the topmost
operation itself succeeded.
The Deno layer writes those events to NDJSON before normalizing them into scenarios.

`crer_input_set_process_opacity(uint32_t pid, uint32_t alpha)` sets the visible, unowned CfT window's opacity
using `WS_EX_LAYERED` and `SetLayeredWindowAttributes(LWA_ALPHA)`. Alpha ranges from 0 (transparent) to 255 (opaque).
It preserves other extended styles, focus, and Z order. Repeated calls with the same alpha do not change the window.
This additive export retains ABI version 2; rebuild the DLL before using `browser.window.opacity` below 1.
