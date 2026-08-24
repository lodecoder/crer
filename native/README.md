# Native record bridge

Publish with .NET 10 Native AOT (Visual Studio Build Tools with MSVC and Windows SDK required):

```text
dotnet publish native/Crer.WinInput.csproj -c Release -r win-x64
```

The DLL exports the `crer_input_*` C ABI required by Deno FFI. It records Raw Input into a bounded in-memory
queue and filters mouse input by the `Chrome_WidgetWin_*` window of the target CfT process and keyboard input by its
foreground window. Hidden Chrome message windows are deliberately excluded from the target lookup.
The Deno layer writes those events to NDJSON before normalizing them into scenarios.
