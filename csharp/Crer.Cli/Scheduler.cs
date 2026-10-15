namespace Crer;

/// <summary>Ordered worker scheduling with cooperative cancellation and complete sibling draining.</summary>
public static class Scheduler
{
    public static async Task<List<TResult>> MapWithConcurrency<T, TResult>(IReadOnlyList<T> values, int maximum,
        Func<T, Task<TResult>> action, Func<TResult, bool>? shouldStop = null, Action? onError = null)
    {
        var results = new TResult[values.Count];
        var completed = new bool[values.Count];
        var next = 0;
        var stopped = false;
        var state = new object();
        // JavaScript starts all initial workers before awaiting even an already resolved
        // promise. This barrier retains that behavior for synchronously completed Tasks.
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Worker()
        {
            while (true)
            {
                int index;
                lock (state)
                {
                    if (stopped || next >= values.Count) return;
                    index = next++;
                }
                Task<TResult> pending;
                try { pending = action(values[index]); }
                catch (Exception error) { pending = Task.FromException<TResult>(error); }
                await initialized.Task;
                try
                {
                    var result = await pending;
                    results[index] = result;
                    completed[index] = true;
                    if (shouldStop?.Invoke(result) == true) lock (state) stopped = true;
                }
                catch
                {
                    lock (state) stopped = true;
                    onError?.Invoke();
                    throw;
                }
            }
        }
        var workers = Enumerable.Range(0, Math.Min(Math.Max(1, maximum), values.Count)).Select(_ => Worker()).ToArray();
        initialized.SetResult();
        try { await Task.WhenAll(workers); }
        catch
        {
            // All workers have drained, including cleanup in their finally blocks.
            // Keep the original worker ordering when selecting an exception.
            foreach (var worker in workers)
                if (worker.IsFaulted) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(worker.Exception!.InnerExceptions[0]).Throw();
            throw;
        }
        return results.Where((_, index) => completed[index]).ToList();
    }

    public static async Task<List<TResult>> MapWithCancellation<T, TResult>(IReadOnlyList<T> values, int maximum,
        Func<T, CancellationToken, Task<TResult>> action, Func<TResult, bool> shouldAbort, CancellationToken parent = default)
    {
        using var group = CancellationTokenSource.CreateLinkedTokenSource(parent);
        return await MapWithConcurrency(values, maximum, value => action(value, group.Token), result =>
        {
            if (shouldAbort(result)) group.Cancel();
            return group.IsCancellationRequested;
        }, () => group.Cancel());
    }
}

/// <summary>A run-wide leaf gate shared by all nested scheduler groups.</summary>
public sealed class WorkGate : IDisposable
{
    private readonly SemaphoreSlim gate;
    public WorkGate(int limit)
    {
        if (limit < 1) throw new ArgumentException("semaphore limit must be positive", nameof(limit));
        gate = new(limit, limit);
    }
    public async Task<T?> Run<T>(Func<Task<T>> action, CancellationToken token = default) where T : class
    {
        try { await gate.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        try
        {
            // SemaphoreSlim can complete a queued waiter concurrently with cancellation.
            // Recheck before dispatch so a cancelled leaf never begins execution.
            if (token.IsCancellationRequested) return null;
            return await action();
        }
        finally { gate.Release(); }
    }
    public void Dispose() => gate.Dispose();
}

/// <summary>Same-profile work serializes before it enters the global leaf gate.</summary>
public sealed class KeyedWorkLock
{
    private sealed class Entry { public readonly WorkGate Gate = new(1); public int Users; }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object state = new();
    public int Count { get { lock (state) return entries.Count; } }
    public async Task<T?> Run<T>(string key, Func<Task<T>> action, CancellationToken token = default) where T : class
    {
        Entry entry;
        lock (state)
        {
            if (!entries.TryGetValue(key, out entry!)) entries.Add(key, entry = new());
            entry.Users++;
        }
        try { return await entry.Gate.Run(action, token); }
        finally
        {
            lock (state)
            {
                if (--entry.Users == 0) { entries.Remove(key); entry.Gate.Dispose(); }
            }
        }
    }
}
