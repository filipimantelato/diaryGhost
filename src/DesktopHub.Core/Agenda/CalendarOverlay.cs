namespace DesktopHub.Core.Agenda;

/// <summary>Item somente leitura sobreposto ao calendário por outro módulo (nada é copiado para a agenda).</summary>
/// <param name="Page">Página aberta ao clicar (ex.: "finance").</param>
/// <param name="Done">Já resolvido (pago, confirmado): aparece apagado.</param>
public sealed record CalendarOverlayItem(DateOnly Date, string Title, string Detail, string Color, string Page, bool Done);

/// <summary>Porta: módulos publicam itens para o calendário mostrar (ex.: vencimentos de finanças).</summary>
public interface ICalendarOverlaySource
{
    /// <summary>Nome do filtro no calendário.</summary>
    string Name { get; }

    IReadOnlyList<CalendarOverlayItem> Between(DateOnly first, DateOnly last);

    /// <summary>Chamado quando os itens mudam.</summary>
    IDisposable Watch(Action changed);
}
