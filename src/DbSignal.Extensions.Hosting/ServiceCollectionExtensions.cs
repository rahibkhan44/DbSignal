using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DbSignal.Hosting;

/// <summary>Registers DbSignal with the dependency-injection container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a change feed and the background service that drives it.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddDbSignal(o =>
    /// {
    ///     o.UseFeed(_ => SqlServerFeed.For(cs).Watch("dbo.Products").Build());
    ///     o.RequireAtLeast(ChangeDetail.KeysChanged);
    /// })
    /// .AddHandler&lt;ProductCacheInvalidator&gt;();
    /// </code>
    /// </example>
    /// <param name="services">The container.</param>
    /// <param name="configure">Configures the feed.</param>
    /// <exception cref="InvalidOperationException">No feed was supplied via <c>UseFeed</c>.</exception>
    public static DbSignalBuilder AddDbSignal(
        this IServiceCollection services,
        Action<DbSignalOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new DbSignalOptions();
        configure(options);

        if (options.FeedFactory is null)
        {
            throw new InvalidOperationException(
                "No feed was configured. Call UseFeed(...) inside AddDbSignal, for example: " +
                "o.UseFeed(_ => SqliteFeed.For(connectionString).Build());");
        }

        services.AddSingleton(options);
        services.AddSingleton(sp => options.FeedFactory(sp));

        // In-memory unless the application supplies something durable. Deliberately not a
        // file store by default: writing checkpoints somewhere the developer did not choose
        // is the kind of surprise that shows up as a stale resume months later.
        services.TryAddSingleton<ICheckpointStore, InMemoryCheckpointStore>();

        services.AddHostedService<ChangeFeedHostedService>();

        return new DbSignalBuilder(services);
    }
}

/// <summary>Continues configuration after <see cref="ServiceCollectionExtensions.AddDbSignal"/>.</summary>
public sealed class DbSignalBuilder
{
    internal DbSignalBuilder(IServiceCollection services) => Services = services;

    /// <summary>The container being configured.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Registers a handler. Several may be registered; each sees every batch, and one
    /// throwing does not stop the others.
    /// </summary>
    /// <typeparam name="THandler">The handler type. Resolved per batch from a fresh scope.</typeparam>
    public DbSignalBuilder AddHandler<THandler>()
        where THandler : class, IChangeHandler
    {
        Services.AddScoped<IChangeHandler, THandler>();
        return this;
    }

    /// <summary>Registers a handler written inline, for small reactions.</summary>
    /// <param name="handle">Called for every batch. Must be idempotent — delivery is at-least-once.</param>
    public DbSignalBuilder AddHandler(Func<ChangeBatch, CancellationToken, Task> handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Services.AddScoped<IChangeHandler>(_ => new DelegateChangeHandler(handle));
        return this;
    }

    /// <summary>Stores checkpoints somewhere durable, replacing the in-memory default.</summary>
    /// <typeparam name="TStore">The store implementation.</typeparam>
    public DbSignalBuilder UseCheckpointStore<TStore>()
        where TStore : class, ICheckpointStore
    {
        Services.RemoveAll<ICheckpointStore>();
        Services.AddSingleton<ICheckpointStore, TStore>();
        return this;
    }

    /// <summary>Stores checkpoints in an instance you supply.</summary>
    /// <param name="store">The store.</param>
    public DbSignalBuilder UseCheckpointStore(ICheckpointStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Services.RemoveAll<ICheckpointStore>();
        Services.AddSingleton(store);
        return this;
    }

    private sealed class DelegateChangeHandler : IChangeHandler
    {
        private readonly Func<ChangeBatch, CancellationToken, Task> _handle;

        public DelegateChangeHandler(Func<ChangeBatch, CancellationToken, Task> handle) => _handle = handle;

        public Task HandleAsync(ChangeBatch batch, CancellationToken ct = default) => _handle(batch, ct);
    }
}
