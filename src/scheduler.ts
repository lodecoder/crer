export async function mapWithConcurrency<T, R>(
  values: readonly T[],
  maxParallel: number,
  action: (value: T) => Promise<R>,
): Promise<R[]> {
  const results = new Array<R>(values.length);
  let next = 0;
  const worker = async () => {
    while (true) {
      const index = next++;
      if (index >= values.length) return;
      results[index] = await action(values[index]);
    }
  };
  await Promise.all(
    Array.from({ length: Math.min(Math.max(1, maxParallel), values.length) }, worker),
  );
  return results;
}
