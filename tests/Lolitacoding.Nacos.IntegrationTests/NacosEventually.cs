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
    /// <param name="timeout">
    /// How long to keep reading. The default is the read budget below, which covers a write the
    /// server is still applying. A caller that waits on a server-side index built after the write
    /// asks for longer, because the wait then spans the index task rather than the write.
    /// </param>
    /// <returns>
    /// The first value that satisfies <paramref name="isSatisfied"/>, or the last value read when
    /// none did, so that the caller's own assertion reports the state it observed.
    /// </returns>
    /// <exception cref="NacosException">
    /// No read succeeded, so there is no state to report. The failure itself is the answer, and
    /// rethrowing it says what went wrong where returning <see langword="default"/> would report it
    /// as a value the caller never read.
    /// </exception>
    /// <remarks>
    /// A read that fails is repeated rather than reported. That is the same eventual consistency the
    /// successful reads are waiting on: the server refuses a read while it is still applying the
    /// write ("requested file is being modified", or a version it cannot read back yet), and a
    /// connection that is coming back refuses requests until it has. A read that never succeeds is
    /// still reported, by rethrowing the last failure, so a genuine one is not waited away.
    /// </remarks>
    public static async Task<T> BecomesAsync<T>(
        Func<Task<T>> read,
        Func<T, bool> isSatisfied,
        CancellationToken ct,
        TimeSpan? timeout = null)
        where T : notnull
    {
        T? last = default;
        bool observed = false;
        NacosException? failure = null;
        int attempts = timeout is { } budget
            ? Math.Max(1, (int)Math.Ceiling(budget / Interval))
            : ReadAttempts;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                last = await read().ConfigureAwait(false);
                observed = true;

                if (isSatisfied(last))
                {
                    return last;
                }
            }
            catch (NacosException ex)
            {
                // The server refused the read because it has not applied the write yet, or because
                // the connection is still coming back; both are waited out rather than reported.
                failure = ex;
            }

            await Task.Delay(Interval, ct).ConfigureAwait(false);
        }

        return observed ? last! : throw failure!;
    }

    /// <summary>
    /// Waits for a subscription to report a state the caller is waiting for.
    /// </summary>
    /// <typeparam name="TChange">The state the subscription yields.</typeparam>
    /// <param name="listener">The subscription being waited on.</param>
    /// <param name="isSatisfied">Reports whether a state is the one being waited for.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes once the state has been reported.</returns>
    /// <exception cref="TimeoutException">
    /// The listener ended, or no matching state was reported within the time allowed.
    /// </exception>
    /// <remarks>
    /// This is the counterpart of <see cref="PushedAsync"/> for a change that has already been applied
    /// and cannot be applied again — a prompt version that may only be published once, or a deletion.
    /// A subscription's own refresh interval is what makes it fire, so the wait is the same bounded
    /// watch over the subscription, without a change to repeat.
    /// </remarks>
    public static async Task ReportsAsync<TChange>(
        IAsyncEnumerator<TChange> listener,
        Func<TChange, bool> isSatisfied,
        CancellationToken ct)
    {
        Task<bool>? pending = null;

        for (int attempt = 0; attempt < ChangeAttempts; attempt++)
        {
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

        // The state never arrived. The outstanding read is awaited rather than abandoned: leaving it
        // in flight would make the listener impossible to dispose, which replaces this failure with a
        // disposal error. The caller's token bounds the wait.
        if (pending is not null)
        {
            await pending.ConfigureAwait(false);
        }

        throw new TimeoutException("The change was not reported by the subscription within the time allowed.");
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

    /// <summary>
    /// Applies a fixture write until the server takes it.
    /// </summary>
    /// <typeparam name="T">The value the write answers with.</typeparam>
    /// <param name="write">Issues the write; it must be safe to issue more than once.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The value the write answered with.</returns>
    /// <remarks>
    /// <para>
    /// A RAD agent publish is a write the server reads back before it answers, and that read has a
    /// window in which it can miss the version the write just created — the server answers with an
    /// error of its own rather than refusing the request. The write is repeated because the request
    /// is idempotent for an identical payload: a version that already exists is not created a second
    /// time. The repeat is not answered with the first attempt's state, though: a version the failed
    /// attempt left as a draft is only submitted when the request sets <c>autoSubmit</c>, so a caller
    /// that needs the version online sets it.
    /// </para>
    /// <para>
    /// This is for a write that sets up a test whose subject is something else. A write a test is
    /// *about* is issued directly, so that a failure is reported rather than retried away.
    /// </para>
    /// </remarks>
    public static Task<T> WritesAsync<T>(Func<Task<T>> write, CancellationToken ct)
        where T : notnull
        => BecomesAsync(write, _ => true, ct);
}
