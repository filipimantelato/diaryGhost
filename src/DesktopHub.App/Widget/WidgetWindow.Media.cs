using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopHub.App.Media;
using DesktopHub.Core.Media;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Widget;

/// <summary>Item de chip de fonte de mídia. <see cref="Id"/> null representa "Auto".</summary>
public sealed record SourceChip(string? Id, string Label, bool IsSelected, string Glyph, string Tooltip)
{
    public Visibility GlyphVisibility => Glyph.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string AutomationName => IsSelected ? $"{Label} (selecionado)" : Label;
}

public partial class WidgetWindow
{
    private readonly MediaService _media;
    private readonly IMasterVolume _volume;
    private readonly DispatcherTimer _progressTimer;
    private MediaSessionSnapshot? _shownMedia;
    private IReadOnlyList<MediaSessionSnapshot> _shownSessions = [];
    private bool _shownPinned;
    private bool _updatingVolume;
    private bool _equalizerRunning;
    private string? _artKey;
    private ImageBrush? _artBrush;

    private void InitializeMedia()
    {
        Island.MouseEnter += (_, _) =>
        {
            UpdateProgress();
            UpdateTimers();
        };
        Island.MouseLeave += (_, _) => UpdateTimers();
        ShowMedia(new ActiveMediaChanged(_media.Active, _media.Sessions, _media.IsPinnedByUser));
        ShowVolume(_volume.Current);
    }

    // ---------- Mídia ----------

    internal void ShowMedia(ActiveMediaChanged e)
    {
        _shownMedia = e.Active;
        _shownSessions = e.Sessions;
        _shownPinned = e.IsPinnedByUser;
        RenderMedia();
    }

    /// <summary>Re-renderiza com o último estado (ex.: preferência de capa mudou).</summary>
    internal void RefreshMedia() => RenderMedia();

    /// <summary>
    /// Pincel da capa reaproveitado enquanto a capa não muda: trocar a instância do pincel força o WPF
    /// a recompor a janela inteira (em camadas, por software) — medido como o principal custo do painel.
    /// </summary>
    private ImageBrush? ArtBrushFor(MediaSessionSnapshot? m)
    {
        var key = _settings.Media.ShowArtwork ? m?.Artwork?.Fingerprint : null;
        if (key == _artKey)
        {
            return _artBrush;
        }
        _artKey = key;
        var art = key is null ? null : ArtworkImages.Get(m!.Artwork);
        _artBrush = art is null ? null : new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        _artBrush?.Freeze();
        return _artBrush;
    }

    private void RenderMedia()
    {
        var m = _shownMedia;
        var artBrush = ArtBrushFor(m);

        // Painel Mídia
        if (m is null)
        {
            MediaNowPlaying.Visibility = Visibility.Collapsed;
            MediaProgress.Visibility = Visibility.Collapsed;
            MediaButtons.Visibility = Visibility.Collapsed;
            MediaEmpty.Visibility = Visibility.Visible;
            (MediaEmptyTitle.Text, MediaEmptyBody.Text) = _media.IsAvailable
                ? ("Nada tocando agora", "Toque algo no Spotify, num navegador ou em outro player compatível com os controles de mídia do Windows.")
                : ("Controle de mídia indisponível", "O Windows não forneceu as sessões de mídia neste sistema.");
        }
        else
        {
            MediaNowPlaying.Visibility = Visibility.Visible;
            MediaButtons.Visibility = Visibility.Visible;
            MediaEmpty.Visibility = Visibility.Collapsed;
            // Metadados ausentes ficam visivelmente ausentes — nada é inventado.
            MediaTitle.Text = m.Title ?? "Sem título informado pelo app";
            MediaTitle.Foreground = (Brush)FindResource(m.Title is null ? "TextTertiaryBrush" : "TextPrimaryBrush");
            MediaArtist.Text = m.Artist ?? "";
            MediaArtist.Visibility = m.Artist is null ? Visibility.Collapsed : Visibility.Visible;
            MediaSource.Text = $"{m.AppName} · {StateText(m.State)}{(_shownPinned ? " · fixado" : "")}";
            MediaArt.Background = artBrush;

            PrevButton.Visibility = m.Controls.Previous ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Visibility = m.Controls.Next ? Visibility.Visible : Visibility.Collapsed;
            PlayPauseButton.Content = m.IsPlaying ? "" : "";
            PlayPauseButton.IsEnabled = m.Controls.Supports(MediaCommand.TogglePlayPause);
            MediaProgress.Visibility = m.Timeline is null ? Visibility.Collapsed : Visibility.Visible;
            ProgressSlider.IsEnabled = m.Controls.Seek;
        }

        // Recriar o ItemsSource regera os botões; só faz isso quando os chips realmente mudaram.
        var chips = BuildChips();
        if (SourceChips.ItemsSource is not List<SourceChip> current || !current.SequenceEqual(chips))
        {
            SourceChips.ItemsSource = chips;
        }
        SourceChips.Visibility = _shownSessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Cartão do Início
        HomeMediaTitle.Text = m?.Title ?? (m is null ? "Nada tocando" : "Sem título");
        HomeMediaArtist.Text = m is null ? "" : m.Artist ?? m.AppName;
        HomeMediaArt.Background = artBrush;
        HomeMediaShade.Visibility = artBrush is null ? Visibility.Collapsed : Visibility.Visible;
        HomeMediaPlay.Visibility = m is not null && m.Controls.Supports(MediaCommand.TogglePlayPause) ? Visibility.Visible : Visibility.Collapsed;
        HomeMediaPlay.Content = m?.IsPlaying == true ? "" : "";

        UpdateProgress();
        RenderCompact();
        UpdateTimers();
        RequestResize();
    }

    private List<SourceChip> BuildChips()
    {
        var chips = new List<SourceChip>(_shownSessions.Count + 1)
        {
            new(null, "Auto", !_shownPinned, "", "Segue a fonte preferencial ou o que estiver tocando"),
        };
        foreach (var s in _shownSessions)
        {
            var selected = _shownPinned && s.Id == _shownMedia?.Id;
            chips.Add(new SourceChip(s.Id, s.AppName, selected, s.IsPlaying ? "" : "",
                $"{s.AppName}: {s.Title ?? "sem título"} ({StateText(s.State)})"));
        }
        return chips;
    }

    private static string StateText(PlaybackState state) => state switch
    {
        PlaybackState.Playing => "tocando",
        PlaybackState.Paused => "pausado",
        PlaybackState.Stopped => "parado",
        PlaybackState.Changing => "carregando",
        PlaybackState.Opened => "aberto",
        PlaybackState.Closed => "fechado",
        _ => "estado desconhecido",
    };

    private void RenderCompact()
    {
        var m = _shownMedia;
        if (m is not null)
        {
            var art = ArtBrushFor(m);
            CompactArt.Fill = art;
            CompactArt.Visibility = art is null ? Visibility.Collapsed : Visibility.Visible;
            CompactGlyph.Visibility = art is null ? Visibility.Visible : Visibility.Collapsed;
            CompactGlyph.Text = "";
            CompactText.Text = m.Title is null ? m.AppName : m.Artist is null ? m.Title : $"{m.Title} · {m.Artist}";
            Equalizer.Visibility = Visibility.Visible;
            Equalizer.Opacity = m.IsPlaying ? 1 : 0.35;
        }
        else
        {
            var now = DateTime.Now;
            CompactArt.Visibility = Visibility.Collapsed;
            CompactGlyph.Visibility = Visibility.Visible;
            CompactGlyph.Text = "";
            CompactText.Text = $"{now.ToString("HH:mm", PtBr)} · {now.ToString("ddd, d MMM", PtBr)}";
            Equalizer.Visibility = Visibility.Collapsed;
        }
        RequestResize();
    }

    /// <summary>
    /// Liga timers/animações só quando há algo visível a atualizar:
    /// progresso (1 Hz) no painel Mídia expandido tocando; equalizador (15 fps) no compacto tocando.
    /// </summary>
    private void UpdateTimers()
    {
        var expanded = _appliedLayout == WidgetLayout.Expanded;
        var playing = _shownMedia?.IsPlaying == true;

        var progressNeeded = IsVisible && expanded && _appliedPanel == WidgetPanel.Media && playing && _shownMedia?.Timeline is not null;
        // 1 Hz só com o ponteiro sobre a ilha; senão a cada 5 s. Medido: cada atualização do progresso
        // custa ~18 ms de CPU nesta janela em camadas (benchmark da Fase 2). A posição é sempre calculada
        // a partir do timestamp do app, então intervalos maiores não causam deriva.
        var interval = Island.IsMouseOver ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
        if (_progressTimer.Interval != interval)
        {
            _progressTimer.Interval = interval;
        }
        if (progressNeeded != _progressTimer.IsEnabled)
        {
            _progressTimer.IsEnabled = progressNeeded;
        }

        // Animação contínua é opcional: medida em ~0,18% de CPU (vs ~0,01% parado) — ver benchmark da Fase 2.
        var equalizerNeeded = IsVisible && !expanded && playing && AnimationsAllowed && _settings.Widget.AnimatedPlaybackIndicator;
        if (equalizerNeeded && !_equalizerRunning)
        {
            StartEqualizer();
        }
        else if (!equalizerNeeded && _equalizerRunning)
        {
            StopEqualizer();
        }
    }

    private void UpdateProgress()
    {
        if (_shownMedia is not { Timeline: { } timeline } m || ProgressSlider.IsMouseCaptureWithin)
        {
            return;
        }
        var position = timeline.PositionAt(DateTimeOffset.Now, m.IsPlaying, m.PlaybackRate ?? 1.0);
        ProgressSlider.Maximum = Math.Max(1, timeline.Duration.TotalSeconds);
        ProgressSlider.Value = position.TotalSeconds;
        ElapsedText.Text = FormatTime(position);
        DurationText.Text = FormatTime(timeline.Duration);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", PtBr) : t.ToString(@"m\:ss", PtBr);

    private void StartEqualizer()
    {
        _equalizerRunning = true;
        var bars = new[] { (Bar1, 420), (Bar2, 560), (Bar3, 360) };
        foreach (var (bar, ms) in bars)
        {
            var animation = new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(ms))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            // Janela em camadas: cada quadro custa uma recomposição por software; 15 fps basta para o efeito.
            Timeline.SetDesiredFrameRate(animation, 15);
            ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
    }

    private void StopEqualizer()
    {
        _equalizerRunning = false;
        foreach (var bar in new[] { Bar1, Bar2, Bar3 })
        {
            ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    // ---------- Comandos de mídia ----------

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_shownMedia is not { } m)
        {
            return;
        }
        var command = m.Controls.TogglePlayPause ? MediaCommand.TogglePlayPause : m.IsPlaying ? MediaCommand.Pause : MediaCommand.Play;
        _ = _media.SendAsync(m.Id, command); // sempre para a sessão exibida, nunca para "a última que tocou"
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_shownMedia is { } m)
        {
            _ = _media.SendAsync(m.Id, MediaCommand.Previous);
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_shownMedia is { } m)
        {
            _ = _media.SendAsync(m.Id, MediaCommand.Next);
        }
    }

    private void SourceChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SourceChip chip })
        {
            _media.Select(chip.Id);
        }
    }

    private void ProgressSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_shownMedia is { Controls.Seek: true } m)
        {
            _ = _media.SeekAsync(m.Id, TimeSpan.FromSeconds(ProgressSlider.Value));
        }
    }

    // ---------- Volume do sistema ----------

    internal void ShowVolume(MasterVolumeState state)
    {
        _updatingVolume = true;
        VolumeSlider.IsEnabled = state.Available;
        VolumeSlider.Value = Math.Round(state.Level * 100);
        _updatingVolume = false;
        VolumeText.Text = state.Available ? $"{Math.Round(state.Level * 100):0}" : "—";
        MuteButton.Content = state.Muted ? "" : "";
        MuteButton.IsEnabled = state.Available;
        VolumeCaption.Text = state.Available
            ? state.Muted ? "Som do Windows silenciado" : "Saída de áudio padrão do Windows — volume geral, não o de um app"
            : "Nenhum dispositivo de saída de áudio disponível";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolume)
        {
            return;
        }
        VolumeText.Text = $"{e.NewValue:0}";
        _volume.SetLevel((float)(e.NewValue / 100.0));
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => _volume.SetMuted(!_volume.Current.Muted);
}
