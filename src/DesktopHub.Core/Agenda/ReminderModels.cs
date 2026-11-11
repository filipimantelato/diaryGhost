using DesktopHub.Core.Events;
using DesktopHub.Core.Notifications;

namespace DesktopHub.Core.Agenda;

public enum ReminderSource
{
    Event,
    Task,
    /// <summary>Vencimentos do módulo de finanças (contas, assinaturas, faturas).</summary>
    Finance,
}

/// <summary>
/// Um aviso calculado. <see cref="Key"/> identifica o aviso de forma estável (origem, ocorrência e
/// antecedência), então o mesmo aviso nunca é entregue duas vezes, mesmo após reiniciar.
/// </summary>
/// <param name="Anchor">Início do evento ou prazo da tarefa.</param>
/// <param name="FireAt">Quando avisar (ou fim do adiamento).</param>
/// <param name="SourceUpdatedAt">Última edição da origem: avisos criados "no passado" não contam como atrasados.</param>
public sealed record ReminderTrigger(
    string Key,
    ReminderSource Source,
    Guid SourceId,
    string Title,
    DateTimeOffset Anchor,
    DateTimeOffset FireAt,
    int MinutesBefore,
    bool AllDay,
    DateTimeOffset SourceUpdatedAt,
    bool Snoozed = false)
{
    public static string MakeKey(ReminderSource source, Guid id, DateTimeOffset anchor, int minutesBefore) =>
        $"{source}:{id:N}:{anchor.ToUnixTimeMilliseconds()}:{minutesBefore}";
}

public enum ReminderState
{
    /// <summary>Entregue (ou tentado: o resultado está em <see cref="ReminderLogEntry.Outcome"/>).</summary>
    Fired,
    Snoozed,
    Dismissed,
    Completed,
    /// <summary>Criado depois do horário de aviso e com o compromisso já iniciado: não faz sentido avisar.</summary>
    Skipped,
}

public sealed record ReminderLogEntry(
    string Key,
    ReminderSource Source,
    Guid SourceId,
    string Title,
    DateTimeOffset Anchor,
    DateTimeOffset FireAt,
    ReminderState State,
    DateTimeOffset? SnoozedUntil,
    DeliveryOutcome? Outcome,
    bool Late,
    DateTimeOffset UpdatedAt);

/// <summary>Porta: registro de avisos entregues/adiados e a marca d'água da última avaliação.</summary>
public interface IReminderLog
{
    ReminderLogEntry? Find(string key);
    IReadOnlySet<string> ExistingKeys(IEnumerable<string> keys);
    void Upsert(ReminderLogEntry entry);
    IReadOnlyList<ReminderLogEntry> SnoozedDueBy(DateTimeOffset at);
    IReadOnlyList<ReminderLogEntry> Snoozed();
    IReadOnlyList<ReminderLogEntry> Recent(int count);
    int PruneBefore(DateTimeOffset cutoff);

    /// <summary>Até quando os avisos já foram avaliados. null na primeira execução.</summary>
    DateTimeOffset? Watermark { get; set; }
}

/// <summary>Avisos entregues, adiados ou reagendados (para atualizar telas).</summary>
public sealed record RemindersChanged : IAppEvent;

/// <summary>
/// Outra origem de avisos para o <see cref="ReminderScheduler"/> (ex.: vencimentos de finanças). Reaproveita o registro,
/// a marca d'água e o adiar: a origem só calcula os avisos e o texto do toast.
/// </summary>
public interface IReminderSource
{
    ReminderSource Source { get; }

    /// <summary>Página aberta ao clicar no aviso.</summary>
    string Page { get; }

    /// <summary>Avisos com horário em (fromExclusive, toInclusive]; com <paramref name="updatedSince"/>, só de origens editadas depois.</summary>
    IReadOnlyList<ReminderTrigger> TriggersBetween(DateTimeOffset fromExclusive, DateTimeOffset toInclusive, DateTimeOffset? updatedSince);

    /// <summary>Corpo do toast (sem dados sensíveis: o toast aparece na tela de bloqueio e no histórico do Windows).</summary>
    string Body(ReminderTrigger trigger, DateTimeOffset now);

    /// <summary>Título atual (a origem pode ter sido renomeada desde o aviso).</summary>
    string? CurrentTitle(Guid sourceId);

    /// <summary>Chamado quando os dados da origem mudam (o agendador reavalia).</summary>
    IDisposable Watch(Action changed);
}
