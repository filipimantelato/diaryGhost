using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopHub.Core.Activity;

namespace DesktopHub.Infrastructure.Activity;

/// <summary>
/// Edita os hooks do Claude Code em <c>~/.claude/settings.json</c> (formato oficial: <c>hooks.&lt;Evento&gt;[].hooks[]</c>
/// com <c>type: "command"</c>). Só mexe nas entradas do Diary Ghost (identificadas por <see cref="Marker"/>),
/// preserva todo o resto e faz backup antes de gravar. Só é chamado por um clique explícito do usuário.
/// </summary>
public sealed class ClaudeHooksInstaller(string settingsPath) : IClaudeHooksInstaller
{
    public const string Marker = "--hook claude-code";
    public static readonly string[] Events = ["SessionStart", "UserPromptSubmit", "Notification", "Stop", "SessionEnd"];

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public string SettingsPath => settingsPath;

    public bool IsInstalled()
    {
        var root = Load(out _);
        return root?["hooks"] is JsonObject hooks && Events.All(e => hooks[e] is JsonArray groups && groups.Any(IsOurs));
    }

    public string? Install(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!command.Contains(Marker, StringComparison.Ordinal))
        {
            throw new ArgumentException("O comando precisa conter " + Marker, nameof(command));
        }
        var root = Load(out var existed) ?? new JsonObject();
        var backup = existed ? Backup() : null;
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }
        foreach (var evt in Events)
        {
            if (hooks[evt] is not JsonArray groups)
            {
                groups = [];
                hooks[evt] = groups;
            }
            RemoveOurs(groups);
            groups.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = 5 }),
            });
        }
        Save(root);
        return backup;
    }

    public void Uninstall()
    {
        var root = Load(out var existed);
        if (!existed || root?["hooks"] is not JsonObject hooks)
        {
            return;
        }
        Backup();
        foreach (var evt in Events)
        {
            if (hooks[evt] is JsonArray groups)
            {
                RemoveOurs(groups);
                if (groups.Count == 0)
                {
                    hooks.Remove(evt);
                }
            }
        }
        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }
        Save(root);
    }

    private static bool IsOurs(JsonNode? group) =>
        group?["hooks"] is JsonArray list && list.Any(h => h?["command"] is JsonValue c && c.TryGetValue<string>(out var s) && s.Contains(Marker, StringComparison.Ordinal));

    private static void RemoveOurs(JsonArray groups)
    {
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (IsOurs(groups[i]))
            {
                groups.RemoveAt(i);
            }
        }
    }

    private JsonObject? Load(out bool existed)
    {
        existed = File.Exists(settingsPath);
        if (!existed)
        {
            return null;
        }
        var text = File.ReadAllText(settingsPath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }
        // JSON inválido: lança — nunca sobrescrevemos um arquivo que não entendemos.
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidDataException("O settings.json do Claude Code não é um objeto JSON.");
    }

    private string Backup()
    {
        var backup = $"{settingsPath}.diaryghost-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(settingsPath, backup, overwrite: true);
        return backup;
    }

    private void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temp = settingsPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(temp, settingsPath, overwrite: true);
    }
}
