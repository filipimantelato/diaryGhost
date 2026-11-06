using System.Text.Json;
using DesktopHub.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Infrastructure.Tests;

public sealed class JsonFileLoggerTests
{
    [Fact]
    public void Writes_structured_json_lines_and_respects_minimum_level()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DesktopHubTests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = new JsonFileLoggerProvider(dir, LogLevel.Information))
            {
                var logger = provider.CreateLogger("Teste.Categoria");
#pragma warning disable CA1848, CA1873, CA2254 // teste do provedor, não do estilo de chamada
                logger.LogDebug("não deve aparecer");
                logger.LogInformation("Sincronizou {Count} itens de {Source}", 3, "rss");
                logger.LogError(new InvalidOperationException("falhou"), "Erro em {Module}", "clima");
#pragma warning restore CA1848, CA1873, CA2254
            } // Dispose aguarda a gravação

            var lines = Directory.GetFiles(dir, "hub-*.log").SelectMany(File.ReadAllLines).ToList();
            Assert.Equal(2, lines.Count);

            using var info = JsonDocument.Parse(lines[0]);
            Assert.Equal("Information", info.RootElement.GetProperty("level").GetString());
            Assert.Equal("Teste.Categoria", info.RootElement.GetProperty("category").GetString());
            Assert.Equal("3", info.RootElement.GetProperty("Count").GetString());
            Assert.Equal("Sincronizou 3 itens de rss", info.RootElement.GetProperty("message").GetString());

            using var error = JsonDocument.Parse(lines[1]);
            Assert.Equal("System.InvalidOperationException", error.RootElement.GetProperty("exception").GetProperty("type").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
