using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DesktopHub.Core.Activity;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Infrastructure.Activity;

/// <summary>
/// Canal local (named pipe) restrito ao usuário atual: <see cref="PipeOptions.CurrentUserOnly"/> faz o Windows
/// recusar conexões de outros usuários, e o cliente também verifica que o servidor é do mesmo usuário.
/// O nome inclui um hash do SID (e da pasta de dados de teste, se houver), então instâncias isoladas não se misturam.
/// </summary>
public static class ActivityPipe
{
    public static string Name(string? dataDir)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var key = dataDir is null ? sid : sid + "|" + Path.GetFullPath(dataDir).ToUpperInvariant();
        return "DiaryGhost.Activity." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }

    /// <summary>Envia uma linha do contrato. Nunca lança: o app pode não estar aberto, e o hook não pode atrasar o Claude Code.</summary>
    public static bool Send(string pipeName, string line, int timeoutMs = 800)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Servidor do canal: aceita uma conexão por vez, lê uma linha (até 64 KB, 2 s) e entrega ao app.</summary>
public sealed partial class ActivityPipeServer(string pipeName, ILogger<ActivityPipeServer> logger) : IActivityChannel, IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool IsListening { get; private set; }

    public string? LastError { get; private set; }

    public event Action<string>? MessageReceived;

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token), token);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            IsListening = false;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                IsListening = true;
                LastError = null;
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                var line = await ReadLineAsync(server, token).ConfigureAwait(false);
                if (line is not null)
                {
                    MessageReceived?.Invoke(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ex.: outro processo já usa o nome. Registra e tenta de novo com calma.
                IsListening = false;
                LastError = ex.Message;
                LogPipeError(ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                server?.Dispose();
            }
        }
        IsListening = false;
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var buffer = new byte[ActivityContract.MaxMessageBytes];
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                total += read;
                if (Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null; // cliente lento ou mudo: descarta
        }
        var text = Encoding.UTF8.GetString(buffer, 0, total);
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        return newline >= 0 ? text[..newline] : total < buffer.Length ? text : null;
    }

    public void Dispose() => Close();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Canal local de atividade indisponível; nova tentativa em 5 s")]
    private partial void LogPipeError(Exception ex);
}
