using System.Runtime.InteropServices;
using DesktopHub.Core.Events;
using DesktopHub.Core.Media;
using Microsoft.Extensions.Logging;

namespace DesktopHub.Infrastructure.Media;

/// <summary>
/// Volume mestre do dispositivo de saída padrão (o mesmo controle do ícone de som do Windows),
/// via Core Audio. Mudanças feitas por qualquer programa chegam por callback; a troca do dispositivo
/// padrão (fone ↔ caixa) é acompanhada. Todas as chamadas COM ficam na thread que chamou <see cref="Start"/>.
/// </summary>
public sealed partial class CoreAudioMasterVolume : IMasterVolume
{
    private const uint ClsctxAll = 0x17;
    private static readonly Guid AudioEndpointVolumeIid = typeof(IAudioEndpointVolume).GUID;
    private readonly IEventBus _bus;
    private readonly ILogger<CoreAudioMasterVolume> _logger;
    private readonly Guid _eventContext = Guid.NewGuid();
    private readonly Callbacks _callbacks;
    private SynchronizationContext? _owner;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private bool _disposed;

    public CoreAudioMasterVolume(IEventBus bus, ILogger<CoreAudioMasterVolume> logger)
    {
        _bus = bus;
        _logger = logger;
        _callbacks = new Callbacks(this);
    }

    public MasterVolumeState Current { get; private set; } = new(false, 0, false);

    public void Start()
    {
        _owner = SynchronizationContext.Current;
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _enumerator.RegisterEndpointNotificationCallback(_callbacks);
            AttachDefaultEndpoint();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            LogUnavailable(ex);
        }
    }

    public bool SetLevel(float level)
    {
        if (_endpoint is null)
        {
            return false;
        }
        var ctx = _eventContext;
        var ok = _endpoint.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx) >= 0;
        if (ok)
        {
            ReadAndPublish();
        }
        return ok;
    }

    public bool SetMuted(bool muted)
    {
        if (_endpoint is null)
        {
            return false;
        }
        var ctx = _eventContext;
        var ok = _endpoint.SetMute(muted, ref ctx) >= 0;
        if (ok)
        {
            ReadAndPublish();
        }
        return ok;
    }

    private void AttachDefaultEndpoint()
    {
        DetachEndpoint();
        if (_enumerator is null)
        {
            return;
        }
        // HRESULT de erro aqui significa "sem dispositivo de saída" (ex.: tudo desconectado).
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) < 0 || device is null)
        {
            Publish(new MasterVolumeState(false, 0, false));
            return;
        }
        try
        {
            var iid = AudioEndpointVolumeIid;
            if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var obj) < 0)
            {
                Publish(new MasterVolumeState(false, 0, false));
                return;
            }
            _endpoint = (IAudioEndpointVolume)obj;
            _endpoint.RegisterControlChangeNotify(_callbacks);
            ReadAndPublish();
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void DetachEndpoint()
    {
        if (_endpoint is null)
        {
            return;
        }
        _endpoint.UnregisterControlChangeNotify(_callbacks);
        Marshal.ReleaseComObject(_endpoint);
        _endpoint = null;
    }

    private void ReadAndPublish()
    {
        if (_endpoint is null)
        {
            return;
        }
        _endpoint.GetMasterVolumeLevelScalar(out var level);
        _endpoint.GetMute(out var muted);
        Publish(new MasterVolumeState(true, level, muted));
    }

    private void Publish(MasterVolumeState state)
    {
        if (state == Current)
        {
            return;
        }
        Current = state;
        _bus.Publish(new MasterVolumeChanged(state));
    }

    /// <summary>Chamado em thread do Core Audio: só lê a estrutura recebida (sem chamadas COM).</summary>
    private void OnVolumeNotification(IntPtr data)
    {
        if (data == IntPtr.Zero || _disposed)
        {
            return;
        }
        var n = Marshal.PtrToStructure<AudioVolumeNotificationData>(data);
        Publish(new MasterVolumeState(true, n.MasterVolume, n.Muted != 0));
    }

    private void OnDefaultDeviceChanged()
    {
        if (_disposed)
        {
            return;
        }
        // Não chamar COM dentro do callback: reconecta na thread dona.
        void Reattach(object? _)
        {
            try
            {
                AttachDefaultEndpoint();
            }
            catch (COMException ex)
            {
                LogReattachFailed(ex);
            }
        }
        if (_owner is not null)
        {
            _owner.Post(Reattach, null);
        }
        else
        {
            ThreadPool.QueueUserWorkItem(Reattach);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            DetachEndpoint();
            if (_enumerator is not null)
            {
                _enumerator.UnregisterEndpointNotificationCallback(_callbacks);
                Marshal.ReleaseComObject(_enumerator);
                _enumerator = null;
            }
        }
        catch (COMException ex)
        {
            LogReattachFailed(ex);
        }
    }

    /// <summary>Objeto exposto ao Core Audio (CCW). Mantido vivo por referência forte em <see cref="_callbacks"/>.</summary>
    private sealed class Callbacks(CoreAudioMasterVolume owner) : IAudioEndpointVolumeCallback, IMMNotificationClient
    {
        public int OnNotify(IntPtr notifyData)
        {
            owner.OnVolumeNotification(notifyData);
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.Render && role == ERole.Multimedia)
            {
                owner.OnDefaultDeviceChanged();
            }
            return 0;
        }

        public int OnDeviceStateChanged(string deviceId, uint newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Controle de volume do Windows indisponível")]
    private partial void LogUnavailable(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao reconectar ao dispositivo de áudio padrão")]
    private partial void LogReattachFailed(Exception ex);
}
