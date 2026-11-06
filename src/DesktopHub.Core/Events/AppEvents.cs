namespace DesktopHub.Core.Events;

// Catálogo de eventos da Fase 1. Cada fase acrescenta os seus (ver ARCHITECTURE.md §4).

/// <summary>Uma seção das configurações mudou. <see cref="Section"/> usa as constantes de <see cref="Settings.SettingsSections"/>.</summary>
public sealed record SettingsChanged(string Section) : IAppEvent;

/// <summary>O widget ficou visível ou oculto (por ação do usuário ou por política de tela cheia).</summary>
public sealed record WidgetVisibilityChanged(bool IsVisible, WidgetVisibilityReason Reason) : IAppEvent;

public enum WidgetVisibilityReason
{
    User,
    FullscreenPolicy,
}

/// <summary>A janela em primeiro plano entrou ou saiu de tela cheia (jogos, vídeos, apresentações).</summary>
public sealed record ForegroundFullscreenChanged(bool IsFullscreen) : IAppEvent;
