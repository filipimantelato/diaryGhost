using System.Globalization;
using System.Text.Json;
using DesktopHub.Core.Finance;

namespace DesktopHub.Infrastructure.Finance;

/// <summary>Hipóteses de um cenário salvo, em JSON estável (datas yyyy-MM-dd, valores em centavos).</summary>
internal static class ScenarioJson
{
    private sealed record Dto(string Kind, string? Description, string Date, long Amount, int Installments, string? CardId);

    public static string Write(IReadOnlyList<SimulationItem> items) =>
        JsonSerializer.Serialize(items.Select(i => new Dto(i.Kind.ToString(), i.Description, i.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            i.Amount, i.Installments, i.CardId?.ToString())).ToList());

    public static IReadOnlyList<SimulationItem> Parse(string json)
    {
        List<Dto>? list;
        try
        {
            list = JsonSerializer.Deserialize<List<Dto>>(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Cenário ilegível.", ex);
        }
        if (list is null)
        {
            throw new FormatException("Cenário vazio.");
        }
        return [.. list.Select(d => new SimulationItem
        {
            Kind = Enum.Parse<SimulationKind>(d.Kind),
            Description = d.Description ?? "",
            Date = DateOnly.ParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            Amount = d.Amount,
            Installments = d.Installments < 1 ? 1 : d.Installments,
            CardId = d.CardId is { } c ? Guid.Parse(c) : null,
        })];
    }
}
