# Native record bridge

Publish with .NET 10 Native AOT:

```text
cmake -S native -B native/build -A x64
cmake --build native/build --config Release
```

The exported ABI is intentionally present but recording is not implemented yet: `crer_input_start` returns
`ERROR_CALL_NOT_IMPLEMENTED`. This prevents `record` from silently using OS-wide input injection. The next
milestone replaces the stub with the Raw Input message loop and ring buffer specified in `docs/specification.md`.