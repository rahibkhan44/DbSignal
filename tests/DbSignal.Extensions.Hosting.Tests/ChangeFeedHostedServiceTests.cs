using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DbSignal.Hosting.Tests;

/// <summary>
/// Drives the hosted service through a real host, because the promises worth testing here
/// — start-up refusal, checkpoint ordering, handler isolation — are promises about wiring.
/// </summary>
public sealed class ChangeFeedHostedServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static IHost BuildHost(
        Action<DbSignalOptions> configure,
        Action<DbSignalBuilder> register,
        ICheckpointStore? store = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        var dbSignal = builder.Services.AddDbSignal(configure);
        if (store is not null)
        {
            dbSignal.UseCheckpointStore(store);
        }

        register(dbSignal);
        return builder.Build();
    }

    [Fact]
    public async Task Refuses_to_start_when_the_provider_cannot_meet_the_required_detail()
    {
        using var host = BuildHost(
            o =>
            {
                o.UseFeed(_ => new FakeFeed(
                    FakeFeed.Caps(ChangeDetail.DatabaseChanged),
                    (_, ct) => FakeFeed.Idle(ct)));
                o.RequireAtLeast(ChangeDetail.KeysChanged);
            },
            b => b.AddHandler((_, _) => Task.CompletedTask));

        // The whole point of RequireAtLeast: a wiring mistake costs a restart, not six
        // months of an application quietly doing a fraction of its job.
        var start = async () => await host.StartAsync();

        await start.Should().ThrowAsync<CapabilityNotSupportedException>()
                   .WithMessage("*KeysChanged*");
    }

    [Fact]
    public async Task Starts_when_the_provider_meets_the_required_detail()
    {
        using var host = BuildHost(
            o =>
            {
                o.UseFeed(_ => new FakeFeed(
                    FakeFeed.Caps(ChangeDetail.KeysChanged),
                    (_, ct) => FakeFeed.Idle(ct)));
                o.RequireAtLeast(ChangeDetail.KeysChanged);
            },
            b => b.AddHandler((_, _) => Task.CompletedTask));

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Saves_the_checkpoint_after_the_handler_succeeds()
    {
        var store = new InMemoryCheckpointStore();

        using var host = BuildHost(
            o => o.UseFeed(_ => new FakeFeed(
                FakeFeed.Caps(ChangeDetail.KeysChanged),
                (_, _) => FakeFeed.Yielding(FakeFeed.Batch("cp-1")))),
            b => b.AddHandler((_, _) => Task.CompletedTask),
            store);

        await RunToCompletionAsync(host);

        var saved = await store.LoadAsync("default");
        saved!.Value.Value.Should().Be("cp-1");
    }

    [Fact]
    public async Task Does_not_save_the_checkpoint_when_the_handler_throws()
    {
        var store = new InMemoryCheckpointStore();

        using var host = BuildHost(
            o => o.UseFeed(_ => new FakeFeed(
                FakeFeed.Caps(ChangeDetail.KeysChanged),
                (_, _) => FakeFeed.Yielding(FakeFeed.Batch("cp-1")))),
            b => b.AddHandler((_, _) => throw new InvalidOperationException("handler failed")),
            store);

        await RunToCompletionAsync(host);

        // At-least-once means a failed batch must be seen again, not silently skipped.
        // Advancing the checkpoint here would turn every handler bug into data loss.
        var saved = await store.LoadAsync("default");
        saved.Should().BeNull();
    }

    [Fact]
    public async Task One_throwing_handler_does_not_stop_its_siblings()
    {
        var secondRan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = BuildHost(
            o => o.UseFeed(_ => new FakeFeed(
                FakeFeed.Caps(ChangeDetail.KeysChanged),
                (_, _) => FakeFeed.Yielding(FakeFeed.Batch("cp-1")))),
            b => b.AddHandler((_, _) => throw new InvalidOperationException("first handler failed"))
                  .AddHandler((_, _) =>
                  {
                      secondRan.TrySetResult(true);
                      return Task.CompletedTask;
                  }));

        await RunToCompletionAsync(host);

        secondRan.Task.IsCompleted.Should().BeTrue(
            "a handler that throws must not deprive the others of the batch");
    }

    [Fact]
    public async Task Retries_after_the_feed_faults()
    {
        var attempts = 0;
        var handled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = BuildHost(
            o =>
            {
                o.InitialRetryDelay = TimeSpan.FromMilliseconds(10);
                o.MaxRetryDelay = TimeSpan.FromMilliseconds(50);
                o.UseFeed(_ => new FakeFeed(
                    FakeFeed.Caps(ChangeDetail.KeysChanged),
                    (_, _) =>
                        // Fault the first attempt, succeed on the second.
                        ++attempts == 1
                            ? Faulting()
                            : FakeFeed.Yielding(FakeFeed.Batch("cp-after-retry"))));
            },
            b => b.AddHandler((_, _) =>
            {
                handled.TrySetResult(true);
                return Task.CompletedTask;
            }));

        await host.StartAsync();
        var completed = await Task.WhenAny(handled.Task, Task.Delay(Patience));
        await host.StopAsync();

        completed.Should().BeSameAs(handled.Task, "the service should recover from a faulted feed");
        attempts.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Ignores_a_stored_checkpoint_when_the_feed_cannot_resume()
    {
        var store = new InMemoryCheckpointStore();
        await store.SaveAsync("default", new Checkpoint("stale"));

        FakeFeed? feed = null;

        using var host = BuildHost(
            o => o.UseFeed(_ => feed = new FakeFeed(
                FakeFeed.Caps(ChangeDetail.DatabaseChanged, durable: false),
                (_, _) => FakeFeed.Yielding(FakeFeed.Batch("cp-1")))),
            b => b.AddHandler((_, _) => Task.CompletedTask),
            store);

        await RunToCompletionAsync(host);

        // SQLite's position means nothing in a new process. Resuming into it would be
        // comparing two unrelated counters.
        feed!.RequestedStart.Should().Be(Checkpoint.Now);
    }

    private static async Task RunToCompletionAsync(IHost host)
    {
        await host.StartAsync();

        // StopAsync waits for the background task, which has already finished once the
        // feed's enumeration completed, so assertions after this are not racing it.
        await host.StopAsync();
    }

    private static async IAsyncEnumerable<ChangeBatch> Faulting()
    {
        await Task.Yield();
        throw new InvalidOperationException("connection dropped");
#pragma warning disable CS0162 // Unreachable, but required to make this method an iterator.
        yield break;
#pragma warning restore CS0162
    }
}
