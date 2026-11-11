using System.Globalization;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.Settings;

namespace DesktopHub.Core.Finance;

/// <summary>
/// Avisos de vencimento do módulo (Fase 12) entregues pelo <see cref="ReminderScheduler"/>: contas e assinaturas
/// previstas (N dias antes, como definido em cada recorrência) e faturas de cartão com saldo (dias antes configuráveis).
/// Avisos às 09:00 locais. O toast nunca traz valores: aparece na tela de bloqueio e no histórico do Windows.
/// Chaves estáveis (origem, id, vencimento, antecedência): confirmar ou pular a ocorrência faz o aviso sumir; nada repete.
/// </summary>
public sealed class FinanceReminders(FinanceService finance, SettingsService settings, IEventBus bus) : IReminderSource
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public ReminderSource Source => ReminderSource.Finance;

    public string Page => "finance";

    /// <summary>Instante do aviso: dia às 09:00 no fuso local.</summary>
    private DateTimeOffset At(DateOnly day) => ZonedTime.ToUtc(day.ToDateTime(TimeOnly.MinValue) + AgendaService.AllDayReminderTime, finance.LocalZone);

    public IReadOnlyList<ReminderTrigger> TriggersBetween(DateTimeOffset fromExclusive, DateTimeOffset toInclusive, DateTimeOffset? updatedSince)
    {
        var list = new List<ReminderTrigger>();
        if (!finance.IsAvailable || !settings.Finance.DueReminders || toInclusive <= fromExclusive)
        {
            return list;
        }
        var zone = finance.LocalZone;
        var fromDay = DateOnly.FromDateTime(ZonedTime.ToWall(fromExclusive, zone)).AddDays(-1);
        var toDay = DateOnly.FromDateTime(ZonedTime.ToWall(toInclusive, zone)).AddDays(1);

        // Contas e assinaturas: vencimentos cujo aviso (N dias antes) cai na janela.
        foreach (var o in finance.Occurrences(fromDay, toDay.AddDays(FinanceService.MaxRemindDays)))
        {
            var item = o.Item;
            if (item.IsIncome || !o.IsOpen || item.UpdatedAt < updatedSince)
            {
                continue;
            }
            var anchor = At(o.Date);
            var fire = At(o.Date.AddDays(-item.RemindDaysBefore));
            if (fire > fromExclusive && fire <= toInclusive)
            {
                var minutes = item.RemindDaysBefore * 24 * 60;
                list.Add(new ReminderTrigger(ReminderTrigger.MakeKey(ReminderSource.Finance, item.Id, anchor, minutes), ReminderSource.Finance, item.Id,
                    Title(item), anchor, fire, minutes, AllDay: true, item.UpdatedAt));
            }
        }

        // Faturas com saldo a pagar (fechadas, ou a aberta quando o aviso vem antes do fechamento).
        var days = settings.Finance.StatementReminderDays;
        foreach (var card in finance.Cards.Where(c => !c.IsArchived && c.UpdatedAt >= (updatedSince ?? DateTimeOffset.MinValue)))
        {
            foreach (var s in finance.Statements(card.Id))
            {
                if (s.Remaining <= 0 || s.Status is StatementStatus.Paid or StatementStatus.Future || s.Due < fromDay || s.Due > toDay.AddDays(days))
                {
                    continue;
                }
                var anchor = At(s.Due);
                var fire = At(s.Due.AddDays(-days));
                if (fire > fromExclusive && fire <= toInclusive)
                {
                    var minutes = days * 24 * 60;
                    list.Add(new ReminderTrigger(ReminderTrigger.MakeKey(ReminderSource.Finance, card.Id, anchor, minutes), ReminderSource.Finance, card.Id,
                        $"Fatura do cartão {card.Name}", anchor, fire, minutes, AllDay: true, card.UpdatedAt));
                }
            }
        }
        return list;
    }

    private static string Title(RecurringItem item) => item.Kind == RecurringKind.Subscription ? $"Assinatura: {item.Description}" : $"Conta a pagar: {item.Description}";

    public string Body(ReminderTrigger trigger, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var zone = finance.LocalZone;
        var today = DateOnly.FromDateTime(ZonedTime.ToWall(now, zone));
        var due = DateOnly.FromDateTime(ZonedTime.ToWall(trigger.Anchor, zone));
        var days = due.DayNumber - today.DayNumber;
        var when = days switch
        {
            0 => "Vence hoje",
            1 => $"Vence amanhã ({due.ToString("dd/MM", PtBr)})",
            > 1 => $"Vence em {days} dias ({due.ToString("dd/MM", PtBr)})",
            _ => $"Venceu em {due.ToString("dd/MM", PtBr)}",
        };
        var action = finance.Card(trigger.SourceId) is not null ? "registre o pagamento em Finanças › Cartões" : "confirme o pagamento em Finanças";
        return $"{when} · {action}.";
    }

    public string? CurrentTitle(Guid sourceId) =>
        finance.RecurringItem(sourceId) is { } item ? Title(item)
        : finance.Card(sourceId) is { } card ? $"Fatura do cartão {card.Name}"
        : null;

    public IDisposable Watch(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return bus.Subscribe<FinanceChanged>(_ => changed());
    }
}
