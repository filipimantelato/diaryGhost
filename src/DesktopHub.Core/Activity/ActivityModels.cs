using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopHub.Core.Events;

namespace DesktopHub.Core.Activity;

public enum ActivityKind
{
    SessionStarted,
    Working,
    Waiting,
    Finished,
    SessionEnded,
    Test,
    Other,
}

/// <summary>Evento recebido de uma ferramenta autorizada (ex.: Claude Code), já reduzido ao mínimo.</summary>
public sealed record ActivityEvent(Guid Id, string Source, ActivityKind Kind, string? Project, string? Session, string? Message, DateTimeOffset ReceivedAt);

/// <summary>
/// Contrato do canal local (versão 1): uma linha JSON por conexão.
/// <code>{"v":1,"source":"claude-code","event":"Stop","project":"meu-repo","session":"1a2b3c4d","message":null}</code>
/// O remetente já envia só estes campos; o app valida tudo de novo ao receber.
/// </summary>
public static class ActivityContract
{
    public const int Version = 1;
    public const int MaxMessageBytes = 64 * 1024;
    public const int MaxField = 300;

    /// <summary>
    /// Reduz o JSON de um hook do Claude Code ao que o app precisa. Descarta de propósito o texto do prompt,
    /// o caminho da transcrição, entradas/saídas de ferramentas e o caminho completo da pasta (fica só o nome).
    /// Devolve null para JSON inválido ou sem evento.
    /// </summary>
    public static string? FromClaudeHook(string? hookJson, string? fallbackEvent = null)
    {
        JsonObject? input = null;
        if (!string.IsNullOrWhiteSpace(hookJson) && hookJson.Length <= MaxMessageBytes)
        {
            try
            {
                input = JsonNode.Parse(hookJson) as JsonObject;
            }
            catch (JsonException)
            {
                input = null;
            }
        }
        var evt = Str(input, "hook_event_name") ?? fallbackEvent;
        if (string.IsNullOrWhiteSpace(evt))
        {
            return null;
        }
        var cwd = Str(input, "cwd");
        var session = Str(input, "session_id");
        var envelope = new JsonObject
        {
            ["v"] = Version,
            ["source"] = "claude-code",
            ["event"] = Clip(evt, 40),
            ["project"] = cwd is null ? null : Clip(ProjectName(cwd), 80),
            ["session"] = session is null ? null : Clip(session, 8),
            // Só a mensagem de aviso (ex.: "Claude needs your permission to use Bash"); nunca o prompt.
            ["message"] = evt == "Notification" ? Clip(Str(input, "message"), MaxField) : null,
        };
        return envelope.ToJsonString();
    }

    /// <summary>Valida uma linha recebida pelo canal e a converte em evento. Qualquer coisa fora do contrato → null.</summary>
    public static ActivityEvent? Parse(string? line, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxMessageBytes)
        {
            return null;
        }
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (o is null || o["v"] is not JsonValue v || !v.TryGetValue<int>(out var version) || version != Version)
        {
            return null;
        }
        var source = Str(o, "source");
        var evt = Str(o, "event");
        if (source is not ("claude-code" or "diaryghost-test") || evt is null)
        {
            return null;
        }
        var kind = source == "diaryghost-test" ? ActivityKind.Test : evt switch
        {
            "SessionStart" => ActivityKind.SessionStarted,
            "UserPromptSubmit" => ActivityKind.Working,
            "Notification" => ActivityKind.Waiting,
            "Stop" => ActivityKind.Finished,
            "SessionEnd" => ActivityKind.SessionEnded,
            _ => ActivityKind.Other,
        };
        return new ActivityEvent(Guid.CreateVersion7(receivedAt), source, kind,
            Clip(Str(o, "project"), 80), Clip(Str(o, "session"), 8), Clip(Str(o, "message"), MaxField), receivedAt);
    }

    public static string TestMessage() => new JsonObject
    {
        ["v"] = Version,
        ["source"] = "diaryghost-test",
        ["event"] = "Test",
        ["project"] = null,
        ["session"] = null,
        ["message"] = "Evento de teste enviado pelo próprio Diary Ghost.",
    }.ToJsonString();

    private static string ProjectName(string cwd)
    {
        var trimmed = cwd.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    private static string? Str(JsonObject? o, string name) =>
        o?[name] is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static string? Clip(string? text, int max)
    {
        if (text is null)
        {
            return null;
        }
        var clean = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length == 0 ? null : clean.Length <= max ? clean : clean[..(max - 1)] + "…";
    }
}

/// <summary>Porta: canal local por onde chegam as linhas do contrato.</summary>
public interface IActivityChannel
{
    bool IsListening { get; }
    string? LastError { get; }
    void Start();
    void Close();
    event Action<string>? MessageReceived;
}

public interface IActivityStore
{
    void Add(ActivityEvent value);
    IReadOnlyList<ActivityEvent> Recent(int count);
    int PruneBefore(DateTimeOffset cutoff);
}

/// <summary>Porta: hooks do Claude Code no settings.json do usuário (instalar só por ação explícita).</summary>
public interface IClaudeHooksInstaller
{
    string SettingsPath { get; }
    bool IsInstalled();
    /// <summary>Instala (substituindo os nossos, preservando o resto). Devolve o caminho do backup criado, se houver.</summary>
    string? Install(string command);
    void Uninstall();
}

public sealed record ActivityReceived(ActivityEvent Event) : IAppEvent;
