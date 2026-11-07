using System.Text.Json.Nodes;
using DesktopHub.Core.Activity;
using DesktopHub.Infrastructure.Activity;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopHub.Infrastructure.Tests;

public sealed class ActivityInfrastructureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DiaryGhostHookTests", Guid.NewGuid().ToString("N"));

    public ActivityInfrastructureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Command = "\"C:/Apps/DiaryGhost.exe\" --hook claude-code";

    [Fact]
    public void Install_preserves_other_settings_and_hooks_and_is_idempotent()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              // comentário do usuário
              "theme": "dark",
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "say pronto" } ] } ] },
            }
            """);
        var installer = new ClaudeHooksInstaller(path);

        var backup = installer.Install(Command);
        installer.Install(Command);

        Assert.True(File.Exists(backup));
        Assert.True(installer.IsInstalled());
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("dark", (string?)root["theme"]);
        var stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count); // o do usuário + o nosso (uma vez só)
        Assert.Equal("say pronto", (string?)stop[0]!["hooks"]![0]!["command"]);

        installer.Uninstall();
        Assert.False(installer.IsInstalled());
        var after = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Single(after["hooks"]!["Stop"]!.AsArray());
        Assert.Null(after["hooks"]!["Notification"]);
    }

    [Fact]
    public void Install_creates_the_file_when_missing_and_refuses_invalid_json()
    {
        var fresh = new ClaudeHooksInstaller(Path.Combine(_dir, "novo", "settings.json"));
        Assert.Null(fresh.Install(Command));
        Assert.True(fresh.IsInstalled());

        var broken = Path.Combine(_dir, "quebrado.json");
        File.WriteAllText(broken, "{ isto não é json");
        Assert.ThrowsAny<Exception>(() => new ClaudeHooksInstaller(broken).Install(Command));
        Assert.Equal("{ isto não é json", File.ReadAllText(broken)); // intocado
    }

    [Fact]
    public async Task Pipe_delivers_a_line_end_to_end()
    {
        var name = ActivityPipe.Name(_dir);
        using var server = new ActivityPipeServer(name, NullLogger<ActivityPipeServer>.Instance);
        var received = new TaskCompletionSource<string>();
        server.MessageReceived += line => received.TrySetResult(line);
        server.Start();

        var sent = false;
        for (var i = 0; i < 20 && !sent; i++)
        {
            sent = ActivityPipe.Send(name, ActivityContract.TestMessage(), 500);
            if (!sent)
            {
                await Task.Delay(100);
            }
        }

        Assert.True(sent);
        var line = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ActivityKind.Test, ActivityContract.Parse(line, DateTimeOffset.UtcNow)!.Kind);
        Assert.NotEqual(name, ActivityPipe.Name(null)); // instância de teste não colide com a real
    }

    [Fact]
    public void Send_without_server_fails_fast_and_quietly()
    {
        var started = DateTime.UtcNow;
        Assert.False(ActivityPipe.Send(ActivityPipe.Name(Path.Combine(_dir, "ninguém")), "{}", 300));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }
}
