// Test-only executable: accepts Chrome arguments but never exposes a debugging endpoint.
// Only this owned helper recognizes the environment switch; real browser startup is unchanged.
if (int.TryParse(Environment.GetEnvironmentVariable("CRER_STARTUP_STUB_EXIT_CODE"), out var exitCode)) return exitCode;
await Task.Delay(TimeSpan.FromSeconds(60));
return 0;
