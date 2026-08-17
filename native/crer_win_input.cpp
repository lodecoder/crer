#include <windows.h>
#include <atomic>
#include <cstdint>

// ABI v1: the Deno side polls fixed-size events. No callback crosses the FFI boundary.
extern "C" {
struct CrerInputEvent { uint64_t qpc; int32_t x; int32_t y; uint32_t kind; uint32_t data; };
__declspec(dllexport) uint32_t crer_input_abi_version() { return 1; }
__declspec(dllexport) int32_t crer_input_start(HWND) { return ERROR_CALL_NOT_IMPLEMENTED; }
__declspec(dllexport) int32_t crer_input_stop() { return 0; }
__declspec(dllexport) uint32_t crer_input_read(CrerInputEvent*, uint32_t) { return 0; }
__declspec(dllexport) int32_t crer_input_last_error() { return ERROR_CALL_NOT_IMPLEMENTED; }
}
// TODO(v0.2): implement a message-only window, RegisterRawInputDevices, and lock-free ring buffer.