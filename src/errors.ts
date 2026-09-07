export class CliError extends Error {
  override name = "CliError";
}

export class ValidationError extends Error {
  override name = "ValidationError";
}

export class EnvironmentError extends Error {
  override name = "EnvironmentError";
}

export class InterruptedError extends Error {
  override name = "InterruptedError";
}

/** Internal cooperative cancellation used by plan fail-fast and worker deadlines. */
export class ExecutionAbortedError extends Error {
  override name = "ExecutionAbortedError";
}

export function exitCodeFor(error: unknown): 2 | 3 | 5 {
  if (error instanceof InterruptedError) return 5;
  if (error instanceof EnvironmentError) return 3;
  return 2;
}
