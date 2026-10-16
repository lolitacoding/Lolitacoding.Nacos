using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Locking;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Tests.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Locking;

/// <summary>
/// A scripted stand-in for the Nacos lock service.
/// </summary>
/// <remarks>
/// It answers the per-connection probe as the real server does, records every lock operation it was
/// asked to perform, and delegates the answer to the test. An unscripted operation fails the test
/// where it was made, so a client sending something unexpected is reported as such rather than as a
/// timeout.
/// </remarks>
internal sealed class LockTestServer
{
    /// <summary>The address the channel and the client are built for.</summary>
    public static readonly NacosServerAddress Server = NacosServerAddress.Parse("127.0.0.1:8848");

    private readonly List<LockOperationRequest> _operations = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="LockTestServer"/> class.
    /// </summary>
    /// <param name="abilities">
    /// The ability table the connection setup is answered with, or <see langword="null"/> for none.
    /// </param>
    public LockTestServer(Dictionary<string, bool>? abilities = null)
    {
        Stream = new FakeNacosDuplexStream
        {
            SetupAckAbilityTable = abilities ?? [],
        };

        var channel = new FakeNacosChannel(Server, Handle, Stream);
        Factory = new FakeNacosChannelFactory(_ => channel);
    }

    /// <summary>Gets the stream the module connection talks over.</summary>
    public FakeNacosDuplexStream Stream { get; }

    /// <summary>Gets the channel factory the connection was built with.</summary>
    public FakeNacosChannelFactory Factory { get; }

    /// <summary>Gets the lock operations the client sent, in order.</summary>
    public IReadOnlyList<LockOperationRequest> Operations
    {
        get
        {
            lock (_operations)
            {
                return [.. _operations];
            }
        }
    }

    /// <summary>
    /// Gets or sets how the server answers a lock operation.
    /// </summary>
    public Func<LockOperationRequest, LockOperationResponse> OnOperation { get; set; } =
        request => throw new InvalidOperationException(
            $"The test did not script an answer for {request.LockOperationEnum}.");

    /// <summary>
    /// Builds a lock client that talks to this server.
    /// </summary>
    /// <param name="timeProvider">
    /// The clock the client uses, or <see langword="null"/> for the system one. A fake clock is what
    /// makes the renewal intervals testable.
    /// </param>
    /// <returns>The client.</returns>
    public NacosLockClient CreateClient(TimeProvider? timeProvider = null)
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "lock-tests",
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        };

        return new NacosLockClient(
            options,
            [Server],
            Factory,
            timeProvider ?? TimeProvider.System,
            NullLoggerFactory.Instance,
            new Random(1234));
    }

    private Payload Handle(Payload payload)
    {
        if (PayloadCodec.ReadTypeName(payload) == "ServerCheckRequest")
        {
            return FakeServerCheck.Responding()(payload);
        }

        LockOperationRequest request = PayloadCodec.Decode<LockOperationRequest>(payload)!;

        lock (_operations)
        {
            _operations.Add(request);
        }

        return PayloadCodec.Encode(OnOperation(request));
    }
}

/// <summary>
/// The answers a scripted lock service gives, and the waits the tests need to observe them.
/// </summary>
internal static class LockTest
{
    /// <summary>Builds the answer a successful operation gives.</summary>
    /// <param name="reentrantCount">How many times the owner holds the lock afterwards.</param>
    /// <returns>The answer.</returns>
    public static LockOperationResponse Success(int reentrantCount) => new()
    {
        Result = true,
        LockResult = new LockResult
        {
            Success = true,
            ReentrantCount = reentrantCount,
            WaitPosition = -1,
        },
    };

    /// <summary>Builds the answer the server gives while it keeps a caller queued.</summary>
    /// <param name="position">The caller's position in the queue.</param>
    /// <returns>The answer.</returns>
    public static LockOperationResponse Waiting(int position) => new()
    {
        Result = false,
        LockResult = new LockResult
        {
            Success = false,
            WaitPosition = position,
        },
    };

    /// <summary>
    /// Waits until a condition the test is watching for becomes true.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="ct">A token that ends the wait.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    /// <exception cref="TimeoutException">The condition never became true.</exception>
    /// <remarks>
    /// Work a client does on its own — a renewal, a push carried back, a retry — happens off the
    /// call that started it, so it is observed rather than awaited.
    /// </remarks>
    public static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        for (int i = 0; i < 500; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, ct);
        }

        throw new TimeoutException("The expected condition was not observed.");
    }
}
