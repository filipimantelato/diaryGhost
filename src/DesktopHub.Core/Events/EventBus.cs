using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Core.Events;

/// <summary>Marcador para eventos do aplicativo. Eventos devem ser <c>record</c>s imutáveis.</summary>
public interface IAppEvent;

/// <summary>
/// Barramento de eventos em processo. Desacopla módulos: quem publica não conhece quem assina.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Assina eventos do tipo exato <typeparamref name="TEvent"/>.
    /// Com <paramref name="context"/>, o handler é postado nesse contexto (ex.: thread de UI);
    /// sem ele, roda de forma síncrona na thread de quem publicou.
    /// Descarte o retorno para cancelar a assinatura.
    /// </summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler, SynchronizationContext? context = null)
        where TEvent : IAppEvent;

    void Publish<TEvent>(TEvent appEvent) where TEvent : IAppEvent;
}

/// <summary>
/// Implementação sem dependências externas. Política de erros: a exceção de um assinante é
/// registrada e isolada — não chega ao publicador nem impede os demais assinantes.
/// </summary>
public sealed partial class EventBus(ILogger<EventBus> logger) : IEventBus
{
    private readonly Lock _gate = new();
    private ImmutableDictionary<Type, ImmutableArray<Subscription>> _subscriptions =
        ImmutableDictionary<Type, ImmutableArray<Subscription>>.Empty;

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler, SynchronizationContext? context = null)
        where TEvent : IAppEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, typeof(TEvent), e => handler((TEvent)e), context, handler.Method.DeclaringType);
        lock (_gate)
        {
            var list = _subscriptions.GetValueOrDefault(typeof(TEvent), ImmutableArray<Subscription>.Empty);
            _subscriptions = _subscriptions.SetItem(typeof(TEvent), list.Add(subscription));
        }
        return subscription;
    }

    public void Publish<TEvent>(TEvent appEvent) where TEvent : IAppEvent
    {
        ArgumentNullException.ThrowIfNull(appEvent);
        // Leitura sem lock: o dicionário imutável é substituído atomicamente.
        if (!_subscriptions.TryGetValue(typeof(TEvent), out var subscribers))
        {
            return;
        }

        foreach (var subscription in subscribers)
        {
            if (subscription.Context is { } context)
            {
                context.Post(static state =>
                {
                    var (sub, evt) = ((Subscription, IAppEvent))state!;
                    sub.Invoke(evt);
                }, (subscription, (IAppEvent)appEvent));
            }
            else
            {
                subscription.Invoke(appEvent);
            }
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(subscription.EventType, out var list))
            {
                var updated = list.Remove(subscription);
                _subscriptions = updated.IsEmpty
                    ? _subscriptions.Remove(subscription.EventType)
                    : _subscriptions.SetItem(subscription.EventType, updated);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Assinante {Subscriber} falhou ao tratar {EventType}")]
    private partial void LogHandlerFailed(Exception exception, string? subscriber, string eventType);

    private sealed class Subscription(
        EventBus owner, Type eventType, Action<IAppEvent> handler, SynchronizationContext? context, Type? subscriberType)
        : IDisposable
    {
        private volatile bool _disposed;

        public Type EventType { get; } = eventType;
        public SynchronizationContext? Context { get; } = context;

        public void Invoke(IAppEvent appEvent)
        {
            // Um callback já postado não deve rodar após o cancelamento da assinatura.
            if (_disposed)
            {
                return;
            }
            try
            {
                handler(appEvent);
            }
            catch (Exception ex)
            {
                owner.LogHandlerFailed(ex, subscriberType?.FullName, EventType.Name);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            owner.Remove(this);
        }
    }
}
