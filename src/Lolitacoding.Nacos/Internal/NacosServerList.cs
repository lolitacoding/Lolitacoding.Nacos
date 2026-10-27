using System.Collections;

namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// The Nacos servers a client may dial, in the order they should be tried.
/// </summary>
/// <remarks>
/// <para>
/// A list built from <see cref="NacosOptions.ServerAddresses"/> is a snapshot taken when the client
/// is created. An address-server list has the same shape but is replaced as the endpoint reports new
/// members; the connections, the authentication provider and the AI HTTP client all read the current
/// snapshot through this one object, so a refresh reaches every consumer at once.
/// </para>
/// <para>
/// Replacing the list publishes a new array rather than mutating the old one, so a reader that took
/// <see cref="Items"/> keeps a consistent view for as long as it needs it. Code that iterates should
/// take that snapshot once instead of reading <see cref="Count"/> and the indexer in a loop, because
/// the two can otherwise observe different generations.
/// </para>
/// </remarks>
internal sealed class NacosServerList : IReadOnlyList<NacosServerAddress>
{
    private readonly TaskCompletionSource _populated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private NacosServerAddress[] _items;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServerList"/> class.
    /// </summary>
    /// <param name="items">The addresses to start with.</param>
    /// <param name="isDynamic">
    /// Whether an address server may replace the list later. A dynamic list may start empty; a
    /// fixed one may not, because nothing would ever fill it.
    /// </param>
    /// <param name="source">
    /// Where the list comes from, for diagnostics, such as the address-server URL.
    /// </param>
    public NacosServerList(
        IReadOnlyList<NacosServerAddress> items,
        bool isDynamic = false,
        string? source = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items = [.. items];
        IsDynamic = isDynamic;
        Source = source;

        if (_items.Length > 0)
        {
            _populated.TrySetResult();
        }
    }

    /// <summary>Raised after <see cref="Replace"/> published a different set of addresses.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets a value indicating whether an address server may replace this list.</summary>
    public bool IsDynamic { get; }

    /// <summary>Gets where the list comes from, or <see langword="null"/> for a configured list.</summary>
    public string? Source { get; }

    /// <summary>Gets the current addresses, as one consistent snapshot.</summary>
    public IReadOnlyList<NacosServerAddress> Items => Volatile.Read(ref _items);

    /// <inheritdoc />
    public int Count => Items.Count;

    /// <inheritdoc />
    public NacosServerAddress this[int index] => Items[index];

    /// <summary>
    /// Determines whether an address is in the current list.
    /// </summary>
    /// <param name="address">The address to look for.</param>
    /// <returns><see langword="true"/> when the same HTTP endpoint is listed.</returns>
    /// <remarks>
    /// A refresh parses the endpoint's answer into new objects, so a connection cannot ask whether
    /// the instance it dialed is still present; it asks whether the endpoint it dialed still is.
    /// </remarks>
    public bool Contains(NacosServerAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        foreach (NacosServerAddress candidate in Items)
        {
            if (string.Equals(
                candidate.HttpBaseAddress.AbsoluteUri,
                address.HttpBaseAddress.AbsoluteUri,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces the addresses and raises <see cref="Changed"/> when the set differs.
    /// </summary>
    /// <param name="items">The addresses the address server reported.</param>
    /// <returns><see langword="true"/> when the list changed.</returns>
    /// <remarks>
    /// The order is not part of the comparison: an endpoint that merely reorders the same servers
    /// has not changed the cluster, and reconnecting for that would drop a healthy connection for
    /// nothing.
    /// </remarks>
    public bool Replace(IReadOnlyList<NacosServerAddress> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        NacosServerAddress[] next = [.. items];

        if (IsSameSet(Items, next))
        {
            return false;
        }

        Volatile.Write(ref _items, next);

        if (next.Length > 0)
        {
            _populated.TrySetResult();
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return true;
    }

    /// <summary>
    /// Waits until the list has at least one address.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="timeProvider">The clock that measures the wait.</param>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>A task that completes once an address is available.</returns>
    /// <exception cref="NacosConnectionException">
    /// No address arrived within <paramref name="timeout"/>, so there is nothing to dial.
    /// </exception>
    public async Task WaitForItemsAsync(
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (Count > 0)
        {
            return;
        }

        using var budget = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        try
        {
            await _populated.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NacosConnectionException(BuildTimeoutMessage(timeout));
        }
    }

    /// <summary>
    /// Describes why waiting for the first address gave up.
    /// </summary>
    /// <param name="timeout">How long the wait was given.</param>
    /// <returns>The message for the exception.</returns>
    private string BuildTimeoutMessage(TimeSpan timeout) =>
        Source is null
            ? "No Nacos server address is configured."
            : FormattableString.Invariant(
                $"The address server {Source} did not return a server list within {timeout.TotalSeconds} seconds.");

    /// <inheritdoc />
    public IEnumerator<NacosServerAddress> GetEnumerator() => Items.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Compares two lists as sets of HTTP endpoints.
    /// </summary>
    /// <param name="left">The current addresses.</param>
    /// <param name="right">The addresses just read.</param>
    /// <returns><see langword="true"/> when both hold the same endpoints.</returns>
    private static bool IsSameSet(
        IReadOnlyList<NacosServerAddress> left,
        NacosServerAddress[] right)
    {
        if (left.Count != right.Length)
        {
            return false;
        }

        var keys = new HashSet<string>(left.Count, StringComparer.OrdinalIgnoreCase);

        foreach (NacosServerAddress address in left)
        {
            keys.Add(address.HttpBaseAddress.AbsoluteUri);
        }

        foreach (NacosServerAddress address in right)
        {
            if (!keys.Remove(address.HttpBaseAddress.AbsoluteUri))
            {
                return false;
            }
        }

        return keys.Count == 0;
    }
}
