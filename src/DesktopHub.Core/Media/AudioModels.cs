using DesktopHub.Core.Events;

namespace DesktopHub.Core.Media;

/// <summary>Volume mestre do dispositivo de saída padrão (0–1). Não é o volume de um app.</summary>
public sealed record MasterVolumeState(bool Available, float Level, bool Muted);

public sealed record MasterVolumeChanged(MasterVolumeState State) : IAppEvent;

/// <summary>Porta para o volume do sistema (Core Audio na Infrastructure).</summary>
public interface IMasterVolume : IDisposable
{
    MasterVolumeState Current { get; }

    void Start();

    bool SetLevel(float level);

    bool SetMuted(bool muted);
}
