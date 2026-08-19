export async function mapWithConcurrency<T, R>(
  values: readonly T[],
  maxParallel: number,
  action: (value: T) => Promise<R>,
  shouldStop?: (result: R) => boolean,
): Promise<R[]> {
  const results = new Array<R>(values.length);
  let next = 0;
  let stopped = false;
  const worker = async () => {
    while (true) {
      if (stopped) return;
      const index = next++;
      if (index >= values.length) return;
      const result = await action(values[index]);
      results[index] = result;
      if (shouldStop?.(result)) stopped = true;
    }
  };
  await Promise.all(
    Array.from({ length: Math.min(Math.max(1, maxParallel), values.length) }, worker),
  );
  return results.filter((result): result is R => result !== undefined);
}
