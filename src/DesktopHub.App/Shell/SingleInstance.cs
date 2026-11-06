namespace DesktopHub.App.Shell;

/// <summary>
/// Garante uma única instância por sessão do Windows. Uma segunda execução apenas sinaliza a primeira
/// — para abrir a janela principal ou, com <c>--quit</c>, para encerrar graciosamente — e termina.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly EventWaitHandle _quit;
    private readonly List<RegisteredWaitHandle> _registrations = [];

    private SingleInstance(Mutex mutex, EventWaitHandle activate, EventWaitHandle quit)
    {
        _mutex = mutex;
        _activate = activate;
        _quit = quit;
    }

    /// <param name="requestQuit">Se outra instância existir, pede que ela encerre em vez de abrir a janela.</param>
    /// <returns>A instância dona, ou null se outra já estiver em execução (ela foi sinalizada).</returns>
    public static SingleInstance? TryAcquire(string id, bool requestQuit)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{id}.Instance", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            var signal = requestQuit ? $@"Local\{id}.Quit" : $@"Local\{id}.Activate";
            if (EventWaitHandle.TryOpenExisting(signal, out var existing))
            {
                using (existing)
                {
                    existing.Set();
                }
            }
            return null;
        }
        return new SingleInstance(
            mutex,
            new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{id}.Activate"),
            new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{id}.Quit"));
    }

    /// <summary>Os callbacks rodam numa thread do pool; quem chama deve despachar para a UI.</summary>
    public void Listen(Action onActivate, Action onQuit)
    {
        _registrations.Add(ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false));
        _registrations.Add(ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => onQuit(), null, Timeout.Infinite, executeOnlyOnce: true));
    }

    public void Dispose()
    {
        _registrations.ForEach(r => r.Unregister(null));
        _activate.Dispose();
        _quit.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
