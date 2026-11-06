using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Infrastructure.Logging;

/// <summary>
/// Logger estruturado em JSON Lines (<c>hub-AAAAMMDD.log</c>), rotação diária e retenção por número de
/// arquivos. A escrita acontece numa tarefa em segundo plano: chamar o logger nunca bloqueia a UI em I/O.
/// Somente os campos do template da mensagem são serializados — nunca objetos arbitrários — para reduzir
/// o risco de vazar dados sensíveis.
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private const int Capacity = 10_000;
    private readonly string _directory;
    private readonly int _retainedFiles;
    private readonly LogLevel _minimumLevel;
    private readonly TimeProvider _time;
    private readonly Channel<string> _channel;
    private readonly Task _writer;

    public JsonFileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information, int retainedFiles = 7, TimeProvider? time = null)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        _retainedFiles = Math.Max(1, retainedFiles);
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        // Sob pressão extrema, descarta as entradas mais antigas em vez de crescer sem limite.
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new JsonFileLogger(this, categoryName);

    /// <summary>Encerra a fila e aguarda a gravação das entradas pendentes (com limite de tempo).</summary>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(3));
    }

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimumLevel;

    internal void Enqueue(string line) => _channel.Writer.TryWrite(line);

    internal DateTimeOffset Now => _time.GetUtcNow();

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        string? currentDay = null;
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var line))
                {
                    var day = _time.GetLocalNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    if (day != currentDay || writer is null)
                    {
                        if (writer is not null)
                        {
                            await writer.DisposeAsync().ConfigureAwait(false);
                        }
                        currentDay = day;
                        writer = OpenFile(day);
                        PruneOldFiles();
                    }
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
                if (writer is not null)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (IOException)
        {
            // Sem disco/permissão: não há onde registrar; o app continua funcionando sem log em arquivo.
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private StreamWriter OpenFile(string day)
    {
        var path = Path.Combine(_directory, $"hub-{day}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void PruneOldFiles()
    {
        foreach (var file in new DirectoryInfo(_directory).GetFiles("hub-*.log")
                     .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(_retainedFiles))
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Arquivo aberto por um visualizador; tenta de novo na próxima rotação.
            }
        }
    }

    private sealed class JsonFileLogger(JsonFileLoggerProvider provider, string category) : ILogger
    {
        // Mantém acentos legíveis no arquivo. Seguro aqui: o log é um arquivo local, nunca embutido em HTML.
        private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, WriterOptions))
            {
                json.WriteStartObject();
                json.WriteString("ts", provider.Now);
                json.WriteString("level", logLevel.ToString());
                json.WriteString("category", category);
                if (eventId.Id != 0 || eventId.Name is not null)
                {
                    json.WriteNumber("eventId", eventId.Id);
                }
                json.WriteString("message", formatter(state, exception));

                if (state is IReadOnlyList<KeyValuePair<string, object?>> fields)
                {
                    foreach (var (key, value) in fields)
                    {
                        if (key == "{OriginalFormat}")
                        {
                            json.WriteString("template", value?.ToString());
                            continue;
                        }
                        json.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
                    }
                }

                if (exception is not null)
                {
                    json.WriteStartObject("exception");
                    json.WriteString("type", exception.GetType().FullName);
                    json.WriteString("message", exception.Message);
                    json.WriteString("stack", exception.ToString());
                    json.WriteEndObject();
                }
                json.WriteEndObject();
            }
            provider.Enqueue(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }
    }
}
