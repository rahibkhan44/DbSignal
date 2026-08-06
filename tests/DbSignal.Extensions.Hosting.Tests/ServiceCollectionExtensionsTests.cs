using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbSignal.Hosting.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void Refuses_a_registration_with_no_feed()
    {
        var services = new ServiceCollection();

        var act = () => services.AddDbSignal(o => o.CheckpointKey = "orders");

        act.Should().Throw<InvalidOperationException>().WithMessage("*UseFeed*");
    }

    [Fact]
    public void Uses_the_in_memory_checkpoint_store_unless_told_otherwise()
    {
        var services = new ServiceCollection();
        services.AddDbSignal(o => o.UseFeed(_ => new FakeFeed(
            FakeFeed.Caps(ChangeDetail.KeysChanged),
            (_, ct) => FakeFeed.Idle(ct))));

        using var provider = services.BuildServiceProvider();

        // Deliberately not a file store: writing checkpoints somewhere the developer did
        // not choose shows up months later as a stale resume nobody can explain.
        provider.GetRequiredService<ICheckpointStore>().Should().BeOfType<InMemoryCheckpointStore>();
    }

    [Fact]
    public void A_supplied_checkpoint_store_replaces_the_default()
    {
        var mine = new InMemoryCheckpointStore();
        var services = new ServiceCollection();

        services.AddDbSignal(o => o.UseFeed(_ => new FakeFeed(
                    FakeFeed.Caps(ChangeDetail.KeysChanged),
                    (_, ct) => FakeFeed.Idle(ct))))
                .UseCheckpointStore(mine);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICheckpointStore>().Should().BeSameAs(mine);
    }

    [Fact]
    public void Registers_every_handler_so_each_sees_the_batch()
    {
        var services = new ServiceCollection();

        services.AddDbSignal(o => o.UseFeed(_ => new FakeFeed(
                    FakeFeed.Caps(ChangeDetail.KeysChanged),
                    (_, ct) => FakeFeed.Idle(ct))))
                .AddHandler((_, _) => Task.CompletedTask)
                .AddHandler((_, _) => Task.CompletedTask);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<IChangeHandler>().Should().HaveCount(2);
    }
}
