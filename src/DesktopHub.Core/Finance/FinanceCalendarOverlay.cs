using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.Settings;

namespace DesktopHub.Core.Finance;

/// <summary>
/// Vencimentos no calendário (somente leitura): contas, assinaturas e receitas recorrentes e faturas de cartão.
/// Valores aparecem só fora do modo privacidade.
/// </summary>
public sealed class FinanceCalendarOverlay(FinanceService finance, SettingsService settings, IEventBus bus) : ICalendarOverlaySource
{
    public string Name => "Finanças";

    public IReadOnlyList<CalendarOverlayItem> Between(DateOnly first, DateOnly last)
    {
        var (from, to) = (first, last);
        if (!finance.IsAvailable || to < from)
        {
            return [];
        }
        var hide = settings.Finance.HideValues;
        string Value(long minor, string? currency) => hide ? Money.Masked(currency ?? finance.BaseCurrency) : Money.Format(minor, currency ?? finance.BaseCurrency);
        var list = new List<CalendarOverlayItem>();
        foreach (var o in finance.Occurrences(from, to).Where(o => o.State != OccurrenceState.Skipped))
        {
            var item = o.Item;
            var kind = item.Kind switch
            {
                RecurringKind.Subscription => "Assinatura",
                RecurringKind.Income => "Receita prevista",
                _ => "Conta a pagar",
            };
            var state = o.State switch
            {
                OccurrenceState.Confirmed => item.IsIncome ? "recebida" : "paga",
                OccurrenceState.Overdue => "atrasada",
                _ => "prevista",
            };
            list.Add(new CalendarOverlayItem(o.Date, (item.IsIncome ? "↑ " : "") + item.Description,
                $"{kind} · {(o.IsEstimated ? "≈ " : "")}{Value(o.Amount, finance.CurrencyOfRecurring(item))} · {state}",
                item.IsIncome ? "#8FB573" : "#C9A94E", "finance", o.State == OccurrenceState.Confirmed));
        }
        foreach (var card in finance.Cards.Where(c => !c.IsArchived))
        {
            foreach (var s in finance.Statements(card.Id).Where(s => s.Due >= from && s.Due <= to && s.Status != StatementStatus.Future && (s.AmountDue > 0 || s.Payments > 0)))
            {
                var paid = s.Status == StatementStatus.Paid;
                list.Add(new CalendarOverlayItem(s.Due, $"Fatura {card.Name}",
                    $"Fatura de cartão · {Value(paid ? s.AmountDue : Math.Max(0, s.Remaining), card.Currency)} · {(paid ? "paga" : s.Status == StatementStatus.Open ? "aberta" : s.Status == StatementStatus.Closed ? "fechada" : "em atraso")}",
                    card.Color, "finance", paid));
            }
        }
        return [.. list.OrderBy(i => i.Date).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    public IDisposable Watch(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return new Both(bus.Subscribe<FinanceChanged>(_ => changed()), bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.Finance)
            {
                changed();
            }
        }));
    }

    private sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose()
        {
            a.Dispose();
            b.Dispose();
        }
    }
}
