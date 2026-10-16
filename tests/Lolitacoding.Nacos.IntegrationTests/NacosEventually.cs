namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Waits for a Nacos server to report something, for the properties that are eventually consistent.
/// </summary>
/// <remarks>
/// Two behaviours of a real server make a read-after-write assertion flaky. A write is applied
/// asynchronously — publishing a configuration answers before the value is readable, and a
/// deregistration answers before the instance is gone — so the read has to be repeated. And a
/// listener or subscription is registered with the server only after its current value has been
/// yielded, so a change made in that window is never pushed, which means the change has to be
/// repeated as well. Both repetitions are bounded, so a genuinely missing update still fails.
/// </remarks>
internal static class NacosEventually
{
    /// <summary>The delay between two attempts.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    /// <summary>How many times a read is repeated before the wait is given up.</summary>
    private const int ReadAttempts = 150;

    /// <summary>How many times a change is applied before the wait is given up.</summary>
    private const int ChangeAttempts = 20;

    /// <summary>How long a single application of a change is given to be pushed.</summary>
    private static readonly TimeSpan PushWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Reads until the server reports what the caller is waiting for.
    /// </summary>
    /// <typeparam name="T">The type of the value read.</typeparam>
    /// <param name="read">Issues the read.</param>
    /// <param name="isSatisfied">Reports whether a read is the one being waited for.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="tolerateFailures">
    /// <see langword="true"/> to keep reading while the read itself fails, which is what happens
    /// while a server is still applying a write or a connection is still being re-established.
    /// </param>
    /// <returns>
    /// The first value that satisfies <paramref name="isSatisfied"/>, or the last value read when
    /// none did, so that the caller's own assertion reports the state it observed.
    /// </returns>
    public static async Task<T> BecomesAsync<T>(
        Func<Task<T>> read,
        Func<T, bool> isSatisfied,
        CancellationToken ct,
        bool tolerateFailures = false)
        where T : notnull
    {
        T? last = default;

        for (int attempt = 0; attempt < ReadAttempts; attempt++)
        {
            try
            {
                last = await read().ConfigureAwait(false);

                if (isSatisfied(last))
                {
                    return last;
                }
            }
            catch (NacosException) when (tolerateFailures)
            {
                // The server refused the read because it has not applied the write yet, or because
                // the connection is still coming back; both are waited out rather than reported.
            }

            await Task.Delay(Interval, ct).ConfigureAwait(false);
        }

        return last!;
    }

    /// <summary>
    /// Applies a change until a listener reports it.
    /// </summary>
    /// <typeparam name="TChange">The change the listener yields.</typeparam>
    /// <param name="change">Applies the change; it must be safe to apply more than once.</param>
    /// <param name="listener">The listener or subscription being waited on.</param>
    /// <param name="isSatisfied">Reports whether a change is the one being waited for.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes once the change has been pushed.</returns>
    /// <exception cref="TimeoutException">
    /// The listener ended, or the change was applied <see cref="ChangeAttempts"/> times without
    /// being pushed.
    /// </exception>
    public static async Task PushedAsync<TChange>(
        Func<CancellationToken, Task> change,
        IAsyncEnumerator<TChange> listener,
        Func<TChange, bool> isSatisfied,
        CancellationToken ct)
    {
        Task<bool>? pending = null;

        for (int attempt = 0; attempt < ChangeAttempts; attempt++)
        {
            try
            {
                await change(ct).ConfigureAwait(false);
            }
            catch (NacosException)
            {
                // The listener is registered with a connection that may still be re-establishing, so
                // a refused change is retried rather than reported.
            }

            pending ??= listener.MoveNextAsync().AsTask();

            if (await Task.WhenAny(pending, Task.Delay(PushWindow, ct)).ConfigureAwait(false) != pending)
            {
                continue;
            }

            bool moved = await pending.ConfigureAwait(false);
            pending = null;

            if (!moved)
            {
                throw new TimeoutException("The listener ended before it reported the change.");
            }

            if (isSatisfied(listener.Current))
            {
                return;
            }
        }

        // The change was applied without ever being pushed. The outstanding read is awaited rather
        // than abandoned: leaving it in flight would make the listener impossible to dispose, which
        // replaces this failure with a disposal error. The caller's token bounds the wait.
        if (pending is not null)
        {
            await pending.ConfigureAwait(false);
        }

        throw new TimeoutException("The change was not pushed by the server within the time allowed.");
    }
}
