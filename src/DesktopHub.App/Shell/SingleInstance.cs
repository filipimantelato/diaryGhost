using System.IO.MemoryMappedFiles;
using System.Text;

namespace DesktopHub.App.Shell;

/// <summary>
/// Garante uma única instância por sessão do Windows. Uma segunda execução apenas sinaliza a primeira
/// — para abrir a janela principal (opcionalmente numa página, ex.: vinda de um link <c>diaryghost:</c>)
/// ou, com <c>--quit</c>, para encerrar graciosamente — e termina.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const int PageBytes = 256;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly EventWaitHandle _quit;
    private readonly MemoryMappedFile _page;
    private readonly List<RegisteredWaitHandle> _registrations = [];

    private SingleInstance(Mutex mutex, EventWaitHandle activate, EventWaitHandle quit, MemoryMappedFile page)
    {
        _mutex = mutex;
        _activate = activate;
        _quit = quit;
        _page = page;
    }

    /// <summary>Existe uma instância rodando? (sem sinalizá-la)</summary>
    public static bool IsRunning(string id)
    {
        if (Mutex.TryOpenExisting($@"Local\{id}.Instance", out var existing))
        {
            existing.Dispose();
            return true;
        }
        return false;
    }

    /// <param name="requestQuit">Se outra instância existir, pede que ela encerre em vez de abrir a janela.</param>
    /// <param name="page">Página a abrir na instância existente (já validada por quem chama).</param>
    /// <returns>A instância dona, ou null se outra já estiver em execução (ela foi sinalizada).</returns>
    public static SingleInstance? TryAcquire(string id, bool requestQuit, string? page = null)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{id}.Instance", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            if (!requestQuit && page is not null)
            {
                WritePage(id, page);
            }
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
            new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{id}.Quit"),
            MemoryMappedFile.CreateOrOpen($@"Local\{id}.Page", PageBytes));
    }

    private static void WritePage(string id, string page)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting($@"Local\{id}.Page");
            using var view = mmf.CreateViewAccessor(0, PageBytes);
            var bytes = Encoding.UTF8.GetBytes(page.Length > 64 ? page[..64] : page);
            view.Write(0, bytes.Length);
            view.WriteArray(4, bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sem a página, a janela abre na página atual.
        }
    }

    /// <summary>Lê e limpa a página pedida pela outra execução.</summary>
    private string? TakePage()
    {
        using var view = _page.CreateViewAccessor(0, PageBytes);
        var length = view.ReadInt32(0);
        if (length is <= 0 or > PageBytes - 4)
        {
            return null;
        }
        var bytes = new byte[length];
        view.ReadArray(4, bytes, 0, length);
        view.Write(0, 0);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Os callbacks rodam numa thread do pool; quem chama deve despachar para a UI.</summary>
    public void Listen(Action<string?> onActivate, Action onQuit)
    {
        _registrations.Add(ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(TakePage()), null, Timeout.Infinite, executeOnlyOnce: false));
        _registrations.Add(ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => onQuit(), null, Timeout.Infinite, executeOnlyOnce: true));
    }

    public void Dispose()
    {
        _registrations.ForEach(r => r.Unregister(null));
        _activate.Dispose();
        _quit.Dispose();
        _page.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
