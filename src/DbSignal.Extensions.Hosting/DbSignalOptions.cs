namespace DbSignal.Hosting;

/// <summary>How the hosted feed behaves. Configured through <c>AddDbSignal</c>.</summary>
public sealed class DbSignalOptions
{
    internal Func<IServiceProvider, IChangeFeed>? FeedFactory { get; private set; }

    internal ChangeDetail? RequiredDetail { get; private set; }

    /// <summary>
    /// Identifies this feed in the checkpoint store, so several feeds can share one.
    /// </summary>
    public string CheckpointKey { get; set; } = "default";

    /// <summary>
    /// Where to resume when no checkpoint is stored yet. <see cref="Checkpoint.Now"/> — only
    /// changes from here on — is the right default for a cache or a screen, which needs to
    /// stay current rather than to relive history.
    /// </summary>
    public Checkpoint StartAt { get; set; } = Checkpoint.Now;

    /// <summary>
    /// How long to wait before reconnecting after the feed faults. Doubles on repeated
    /// failures up to <see cref="MaxRetryDelay"/>, so a database that is down does not get
    /// hammered by every workstation at once.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling for the backoff.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether a handler throwing should stop the checkpoint advancing, so the batch is
    /// retried on the next pass.
    /// </summary>
    /// <remarks>
    /// Default <see langword="true"/>: at-least-once means a failed handler should see the
    /// batch again rather than have it silently dropped. Set false when a poison batch
    /// blocking the stream would be worse than losing it.
    /// </remarks>
    public bool RetryFailedBatches { get; set; } = true;

    /// <summary>
    /// Supplies the feed. Provider-neutral on purpose — the hosting package does not
    /// reference any database driver, so adding a provider never widens this package's
    /// dependency graph.
    /// </summary>
    /// <example>
    /// <code>
    /// o.UseFeed(_ => SqlServerFeed.For(connectionString).Watch("dbo.Products").Build());
    /// </code>
    /// </example>
    /// <param name="factory">Creates the feed. Called once, at startup.</param>
    public DbSignalOptions UseFeed(Func<IServiceProvider, IChangeFeed> factory)
    {
        FeedFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>
    /// Declares the minimum detail this application needs. If the configured provider
    /// cannot supply it, <strong>startup fails</strong> with a message naming both tiers.
    /// </summary>
    /// <remarks>
    /// The most valuable line in the configuration. Without it, pointing a "patch the changed
    /// rows" application at SQLite — which can only say "something changed" — produces an app
    /// that runs happily and quietly does a fraction of its job.
    /// </remarks>
    /// <param name="detail">The minimum tier required.</param>
    public DbSignalOptions RequireAtLeast(ChangeDetail detail)
    {
        RequiredDetail = detail;
        return this;
    }
}
