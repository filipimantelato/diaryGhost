using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopHub.App.Media;
using DesktopHub.App.Widget;
using DesktopHub.Core.Events;
using DesktopHub.Core.Media;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Main.Pages;

/// <summary>Página Mídia: o que o Windows expõe, qual sessão está sendo controlada e as preferências.</summary>
public partial class MediaPage : UserControl, IDisposable
{
    private readonly MediaService _media;
    private readonly SettingsService _settings;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _syncing;

    internal MediaPage(MediaService media, SettingsService settings, IEventBus bus)
    {
        _media = media;
        _settings = settings;
        InitializeComponent();
        var ui = SynchronizationContext.Current;
        _subscriptions.Add(bus.Subscribe<ActiveMediaChanged>(_ => Render(), ui));
        _subscriptions.Add(bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.Media)
            {
                Render();
            }
        }, ui));
        Render();
    }

    private void Render()
    {
        var active = _media.Active;
        var sessions = _media.Sessions;
        var pinned = _media.IsPinnedByUser;

        if (active is null)
        {
            NowTitle.Text = _media.IsAvailable ? "Nada tocando agora" : "Controle de mídia indisponível neste sistema";
            NowArtist.Text = "";
            NowDetail.Text = _media.IsAvailable ? "Toque algo no Spotify, num navegador ou em outro player compatível." : "";
            NowButtons.Visibility = Visibility.Collapsed;
            ArtBox.Background = null;
        }
        else
        {
            NowTitle.Text = active.Title ?? "Sem título informado pelo app";
            NowArtist.Text = string.Join(" — ", new[] { active.Artist, active.Album }.Where(s => s is not null));
            NowDetail.Text = $"{active.AppName} · {(active.IsPlaying ? "tocando" : "não está tocando")} · {(pinned ? "fixado por você" : "escolhido automaticamente")}";
            NowButtons.Visibility = Visibility.Visible;
            PrevButton.Visibility = active.Controls.Previous ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Visibility = active.Controls.Next ? Visibility.Visible : Visibility.Collapsed;
            PlayButton.Content = active.IsPlaying ? "" : "";
            PlayButton.IsEnabled = active.Controls.Supports(MediaCommand.TogglePlayPause);
            var art = _settings.Media.ShowArtwork ? ArtworkImages.Get(active.Artwork) : null;
            ArtBox.Background = art is null ? null : new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        }

        var rows = new List<SourceChip> { new(null, "Automático", !pinned, "", "Segue a fonte preferencial ou o que estiver tocando") };
        rows.AddRange(sessions.Select(s => new SourceChip(s.Id, $"{s.AppName}{(s.Title is null ? "" : " — " + s.Title)}",
            pinned && s.Id == active?.Id, s.IsPlaying ? "" : "", $"ID da sessão: {s.Id}")));
        SessionList.ItemsSource = rows;

        // Fonte preferencial: automático + sessões atuais + a preferida salva, mesmo que não esteja aberta agora.
        var preferred = _settings.Media.PreferredSourceId;
        var options = new List<SourceChip> { new(null, "Automático", preferred is null, "", "Sem preferência") };
        var ids = sessions.Select(s => s.Id).ToList();
        if (preferred is not null && !ids.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(preferred);
        }
        options.AddRange(ids.Select(id => new SourceChip(id, MediaSelection.FriendlyAppName(id),
            string.Equals(id, preferred, StringComparison.OrdinalIgnoreCase), "", $"ID: {id}")));
        PreferredList.ItemsSource = options;

        _syncing = true;
        ShowArtwork.IsChecked = _settings.Media.ShowArtwork;
        _syncing = false;
    }

    private void Session_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SourceChip chip })
        {
            _media.Select(chip.Id);
        }
    }

    private void Preferred_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SourceChip chip })
        {
            _settings.UpdateMedia(m => m with { PreferredSourceId = chip.Id });
        }
    }

    private void ShowArtwork_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            _settings.UpdateMedia(m => m with { ShowArtwork = ShowArtwork.IsChecked == true });
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => Send(MediaCommand.TogglePlayPause);

    private void Previous_Click(object sender, RoutedEventArgs e) => Send(MediaCommand.Previous);

    private void Next_Click(object sender, RoutedEventArgs e) => Send(MediaCommand.Next);

    private void Send(MediaCommand command)
    {
        if (_media.Active is { } active)
        {
            _ = _media.SendAsync(active.Id, command);
        }
    }

    public void Dispose()
    {
        _subscriptions.ForEach(s => s.Dispose());
        GC.SuppressFinalize(this);
    }
}
