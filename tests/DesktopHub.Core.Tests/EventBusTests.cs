using DesktopHub.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopHub.Core.Tests;

public sealed class EventBusTests
{
    private sealed record Ping(int Value) : IAppEvent;
    private sealed record Pong : IAppEvent;

    private static EventBus NewBus() => new(NullLogger<EventBus>.Instance);

    [Fact]
    public void Publish_delivers_only_to_subscribers_of_that_type()
    {
        var bus = NewBus();
        var pings = new List<int>();
        var pongs = 0;
        using var a = bus.Subscribe<Ping>(p => pings.Add(p.Value));
        using var b = bus.Subscribe<Pong>(_ => pongs++);

        bus.Publish(new Ping(1));
        bus.Publish(new Ping(2));

        Assert.Equal([1, 2], pings);
        Assert.Equal(0, pongs);
    }

    [Fact]
    public void Disposed_subscription_stops_receiving()
    {
        var bus = NewBus();
        var count = 0;
        var sub = bus.Subscribe<Ping>(_ => count++);

        bus.Publish(new Ping(1));
        sub.Dispose();
        sub.Dispose(); // idempotente
        bus.Publish(new Ping(2));

        Assert.Equal(1, count);
    }

    [Fact]
    public void Failing_handler_does_not_break_publisher_or_other_handlers()
    {
        var bus = NewBus();
        var received = false;
        using var bad = bus.Subscribe<Ping>(_ => throw new InvalidOperationException("boom"));
        using var good = bus.Subscribe<Ping>(_ => received = true);

        var ex = Record.Exception(() => bus.Publish(new Ping(1)));

        Assert.Null(ex);
        Assert.True(received);
    }

    [Fact]
    public void Handler_with_context_is_posted_and_skipped_after_dispose()
    {
        var bus = NewBus();
        var context = new QueueContext();
        var count = 0;
        var sub = bus.Subscribe<Ping>(_ => count++, context);

        bus.Publish(new Ping(1));
        Assert.Equal(0, count);   // ainda não executou: foi postado
        context.RunAll();
        Assert.Equal(1, count);

        bus.Publish(new Ping(2));
        sub.Dispose();             // cancelado antes de o contexto processar
        context.RunAll();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Subscribing_during_publish_does_not_throw()
    {
        var bus = NewBus();
        var inner = 0;
        using var outer = bus.Subscribe<Ping>(_ => bus.Subscribe<Ping>(_ => inner++));

        bus.Publish(new Ping(1));
        bus.Publish(new Ping(2));

        Assert.Equal(1, inner);
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback, object?)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void RunAll()
        {
            while (_queue.TryDequeue(out var item))
            {
                item.Item1(item.Item2);
            }
        }
    }
}
