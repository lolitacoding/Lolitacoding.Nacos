using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Extensions.Configuration;

/// <summary>
/// Serves Nacos configuration through the standard
/// <see cref="Microsoft.Extensions.Configuration.IConfigurationProvider"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// The provider keeps one section per data id and re-merges them whenever a data id changes.
/// Rebuilding the whole view rather than patching the changed keys is what makes removals
/// correct: when an operator deletes a key from a document, its flattened key must disappear,
/// which a key-by-key update would leave behind.
/// </para>
/// <para>
/// Loading is synchronous because the configuration system is synchronous. The read happens on
/// the calling thread and completes before <see cref="Load"/> returns, so an application can
/// depend on the values being present the moment it reads them.
/// </para>
/// </remarks>
internal sealed class NacosConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly NacosConfigurationSourceOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// The flattened section contributed by each data id, keyed by data id.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, string?>> _sections =
        new(StringComparer.Ordinal);

    /// <summary>The live subscriptions, disposed when the provider is disposed.</summary>
    private readonly List<IAsyncDisposable> _subscriptions = [];

    /// <summary>Guards <see cref="_sections"/> and every read of it.</summary>
    private readonly object _sync = new();

    private NacosClient? _client;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigurationProvider"/> class.
    /// </summary>
    /// <param name="options">The source options, already validated.</param>
    public NacosConfigurationProvider(NacosConfigurationSourceOptions options)
    {
        _options = options;
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger("Lolitacoding.Nacos.Extensions.Configuration");
    }

    /// <inheritdoc />
    public override void Load()
    {
        if (_disposed)
        {
            return;
        }

        _client = NacosClient.Create(_options.ToClientOptions(), loggerFactory: _options.LoggerFactory);

        try
        {
            _client.ConnectAsync().GetAwaiter().GetResult();
        }
        catch (NacosException ex) when (_options.Optional)
        {
            // In optional mode the process is expected to keep running against whatever it has
            // locally, so an unreachable server is a warning rather than a startup failure.
            NacosConfigurationLog.ConnectFailed(_logger, ex.Message);
        }

        foreach (string dataId in _options.DataIds)
        {
            Subscribe(dataId);
        }

        Publish();
        NacosConfigurationLog.Loaded(_logger, _options.DataIds.Count, Data.Count);
        NacosConfigurationLog.StartedWatching(
            _logger, _options.DataIds.Count, string.Join(", ", _options.DataIds));
    }

    /// <inheritdoc />
    void IDisposable.Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Dispose the subscriptions before the client: removing a listener talks to the client,
        // and doing it the other way round would leave the callbacks pointing at a dead client.
        foreach (IAsyncDisposable subscription in _subscriptions)
        {
            try
            {
                subscription.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (NacosException)
            {
                // Best-effort: the connection is closing anyway.
            }
        }

        _subscriptions.Clear();

        NacosConfigurationLog.StoppedWatching(_logger, _options.DataIds.Count);

        if (_client is not null)
        {
            _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _client = null;
        }
    }

    /// <summary>
    /// Subscribes to a data id and records the change callback.
    /// </summary>
    /// <param name="dataId">The data id to observe.</param>
    private void Subscribe(string dataId)
    {
        var key = new NacosConfigKey(dataId, _options.GroupName, _options.Namespace);

        try
        {
            // Registering a listener also delivers the current value, so this both seeds the
            // section and arranges to be told about later changes.
            IAsyncDisposable subscription = _client!
                .Config
                .AddListenerAsync(key, (change, _) => OnChangeAsync(dataId, change))
                .GetAwaiter()
                .GetResult();

            _subscriptions.Add(subscription);
        }
        catch (NacosException ex) when (_options.Optional)
        {
            NacosConfigurationLog.SubscribeFailed(_logger, dataId, ex.Message);
        }
    }

    /// <summary>
    /// Applies a change pushed for a data id.
    /// </summary>
    /// <param name="dataId">The data id that changed.</param>
    /// <param name="change">The new value.</param>
    /// <returns>A completed task; the work is synchronous.</returns>
    private Task OnChangeAsync(string dataId, NacosConfigChange change)
    {
        Dictionary<string, string?> section = change.Content is null
            // A deleted data id contributes nothing; its keys must vanish from the view.
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : NacosConfigurationParser.Parse(
                dataId,
                change.Result.ContentType,
                change.Content,
                _logger);

        bool changed;
        bool isInitial;

        lock (_sync)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            isInitial = !_sections.ContainsKey(dataId);
            changed = !SectionsEqual(_sections.GetValueOrDefault(dataId), section);
            _sections[dataId] = section;
        }

        if (isInitial)
        {
            // The value delivered while subscribing only seeds the section; Load publishes once
            // every data id has arrived, so reporting a reload here would claim a change that no
            // consumer can observe.
            return Task.CompletedTask;
        }

        if (changed)
        {
            NacosConfigurationLog.Reloading(_logger, dataId);
            Publish();
        }
        else
        {
            NacosConfigurationLog.ContentUnchanged(_logger, dataId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Rebuilds the public view from the per-data-id sections and signals a reload.
    /// </summary>
    private void Publish()
    {
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);

        lock (_sync)
        {
            // The sections are merged in the order the data ids were declared, so a later data id
            // overrides an earlier one for the same key.
            foreach (string dataId in _options.DataIds)
            {
                if (_sections.TryGetValue(dataId, out Dictionary<string, string?>? section))
                {
                    foreach (KeyValuePair<string, string?> entry in section)
                    {
                        merged[entry.Key] = entry.Value;
                    }
                }
            }
        }

        // Assigning the whole dictionary rather than mutating the existing one means a concurrent
        // reader always sees a complete view, never a half-merged one.
        Data = merged;
        OnReload();
    }

    /// <summary>
    /// Determines whether two sections carry the same keys and values.
    /// </summary>
    /// <param name="current">The section currently held, or <see langword="null"/>.</param>
    /// <param name="candidate">The section that arrived.</param>
    /// <returns><see langword="true"/> when they are equivalent.</returns>
    private static bool SectionsEqual(
        Dictionary<string, string?>? current,
        Dictionary<string, string?> candidate)
    {
        if (current is null || current.Count != candidate.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string?> entry in candidate)
        {
            if (!current.TryGetValue(entry.Key, out string? value)
                || !string.Equals(value, entry.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
