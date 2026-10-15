using Lolitacoding.Nacos.Auth;

namespace Lolitacoding.Nacos.Tests.Auth;

/// <summary>
/// A scripted <see cref="INacosAuthProvider"/> that hands out a configurable sequence of tokens.
/// </summary>
internal sealed class FakeAuthProvider : INacosAuthProvider
{
    private readonly Queue<string?> _tokens;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeAuthProvider"/> class.
    /// </summary>
    /// <param name="tokens">
    /// The tokens to return in order. The final token is repeated once the sequence is exhausted,
    /// so a test only needs to list the values that actually change.
    /// </param>
    public FakeAuthProvider(params string?[] tokens) => _tokens = new Queue<string?>(tokens);

    /// <summary>Gets the number of times a token was requested.</summary>
    public int RequestCount { get; private set; }

    /// <summary>Gets the number of times the cached token was invalidated.</summary>
    public int InvalidateCount { get; private set; }

    /// <summary>Gets a value indicating whether this provider was disposed.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Gets the token most recently handed out.</summary>
    public string? LastToken { get; private set; }

    public ValueTask<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        RequestCount++;

        if (_tokens.Count > 1)
        {
            LastToken = _tokens.Dequeue();
        }
        else if (_tokens.Count == 1)
        {
            // Keep the last value so a later call returns the same token rather than null.
            LastToken = _tokens.Peek();
        }

        return ValueTask.FromResult(LastToken);
    }

    public void Invalidate()
    {
        InvalidateCount++;
        LastToken = null;
    }

    public void Dispose() => Disposed = true;
}
