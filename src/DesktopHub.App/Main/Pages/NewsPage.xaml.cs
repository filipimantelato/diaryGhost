using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopHub.App.Agenda;
using DesktopHub.Core.Agenda;
using DesktopHub.Core.Events;
using DesktopHub.Core.News;
using DesktopHub.Core.Settings;

namespace DesktopHub.App.Main.Pages;

/// <summary>
/// Notícias agrupadas por acontecimento, com fonte, autor, horário de publicação e de consulta.
/// Nada de HTML é renderizado: títulos e resumos já chegam como texto; links abrem no navegador (só http/https).
/// </summary>
public partial class NewsPage : UserControl, IDisposable
{
    private const int PageSize = 60;
    private static readonly int[] IntervalOptions = [15, 30, 60, 120, 240];
    private static readonly int[] RetentionOptions = [3, 7, 14, 30, 60];
    private static string s_filter = "all";

    private readonly NewsService _news;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _searchDelay;
    private string _filter = s_filter;
    private int _shown = PageSize;
    private bool _syncing;

    internal NewsPage(NewsService news, SettingsService settings, IEventBus bus)
    {
        _news = news;
        _settings = settings;
        InitializeComponent();
        _searchDelay = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            _shown = PageSize;
            RenderList();
        };
        var ui = SynchronizationContext.Current;
        _subscriptions.Add(bus.Subscribe<NewsUpdated>(_ => Render(), ui));
        _subscriptions.Add(bus.Subscribe<SettingsChanged>(e =>
        {
            if (e.Section == SettingsSections.News)
            {
                Render();
            }
        }, ui));
        IntervalBox.ItemsSource = IntervalOptions.Select(m => new Choice<int>($"A cada {AgendaFormat.Span(TimeSpan.FromMinutes(m))}", m)).ToList();
        RetentionBox.ItemsSource = RetentionOptions.Select(d => new Choice<int>($"Guardar {d} dias", d)).ToList();
        NewSourceCategory.ItemsSource = Enum.GetValues<NewsCategory>().Select(c => new Choice<NewsCategory>(TrustedSources.Label(c), c)).ToList();
        NewSourceCategory.SelectedIndex = 0;
        Render();
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private void Render()
    {
        var s = _settings.News;
        var enabled = s.Enabled;
        DisabledCard.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        FilterBar.Visibility = Body.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = enabled && !_news.IsRefreshing;
        RefreshButton.Content = _news.IsRefreshing ? "Atualizando…" : "Atualizar";
        SourcesButton.IsEnabled = enabled;

        var now = _time.GetLocalNow();
        var active = _news.Sources.Count(x => x.Enabled);
        var failing = _news.Sources.Count(x => x.Enabled && x.LastError is not null);
        StatusText.Text = !enabled
            ? "Desativado — nenhuma consulta à internet é feita."
            : _news.LastSuccessAt is { } last
                ? $"Atualizado {Relative(last)} · {active} fontes ativas{(failing > 0 ? $" ({failing} com erro)" : "")} · a cada {AgendaFormat.Span(TimeSpan.FromMinutes(s.RefreshMinutes))}"
                : _news.IsRefreshing ? "Buscando as primeiras notícias…" : $"Ainda sem dados · {active} fontes ativas. A primeira busca acontece em instantes.";

        _syncing = true;
        NotifyTopics.IsChecked = s.NotifyFollowedTopics;
        IntervalBox.SelectedIndex = Math.Max(0, ((List<Choice<int>>)IntervalBox.ItemsSource).FindIndex(c => c.Value == s.RefreshMinutes));
        RetentionBox.SelectedIndex = Math.Max(0, ((List<Choice<int>>)RetentionBox.ItemsSource).FindIndex(c => c.Value == s.RetentionDays));
        _syncing = false;
        _ = now;

        if (enabled)
        {
            RenderChips();
            RenderList();
            if (SourcesPanel.Visibility == Visibility.Visible)
            {
                RenderSources();
            }
        }
    }

    private string Relative(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var today = _time.GetLocalNow().Date;
        return local.Date == today ? $"às {local:HH:mm}" : $"{AgendaFormat.Day(local.DateTime, today)} às {local:HH:mm}";
    }

    // ---------- Filtros ----------

    private static readonly (string Key, string Label)[] Filters =
    [
        ("all", "Todas"), ("following", "Seguindo"),
        .. Enum.GetValues<NewsCategory>().Select(c => (c.ToString(), TrustedSources.Label(c))),
        ("saved", "Salvas"), ("favorite", "Favoritas"),
    ];

    private void RenderChips()
    {
        CategoryChips.Children.Clear();
        foreach (var (key, label) in Filters)
        {
            var chip = new Button { Style = (Style)FindResource("Chip"), Content = label, Margin = new Thickness(0, 0, 6, 6), DataContext = new ChipState(key == _filter) };
            chip.Click += (_, _) =>
            {
                _filter = s_filter = key;
                _shown = PageSize;
                Render();
                ListScroll.ScrollToTop();
            };
            CategoryChips.Children.Add(chip);
        }
    }

    private List<string> TopicKeys => [.. _settings.News.FollowedTopics.Select(NewsTitles.Key)];

    private IEnumerable<NewsArticle> Filtered()
    {
        var topics = TopicKeys;
        var sources = _news.Sources.ToDictionary(s => s.Id);
        var query = NewsTitles.Key(SearchBox.Text);
        return _news.Articles.Where(a =>
        {
            if (!sources.TryGetValue(a.SourceId, out var source) || (!source.Enabled && !a.IsSaved && !a.IsFavorite))
            {
                return false;
            }
            if (UnreadOnly.IsChecked == true && a.IsRead)
            {
                return false;
            }
            var ok = _filter switch
            {
                "all" => true,
                "following" => topics.Any(t => NewsTitles.Matches(a.TitleKey, t)),
                "saved" => a.IsSaved,
                "favorite" => a.IsFavorite,
                _ => source.Category.ToString() == _filter,
            };
            return ok && (query.Length == 0 || a.TitleKey.Contains(query, StringComparison.Ordinal) ||
                NewsTitles.Key(a.Summary ?? "").Contains(query, StringComparison.Ordinal) || NewsTitles.Key(source.Name).Contains(query, StringComparison.Ordinal));
        });
    }

    private void RenderList()
    {
        ListPanel.Children.Clear();
        var clusters = Filtered().GroupBy(a => a.ClusterId).ToList();
        foreach (var cluster in clusters.Take(_shown))
        {
            ListPanel.Children.Add(Card([.. cluster]));
        }
        if (clusters.Count > _shown)
        {
            var more = new Button { Style = (Style)FindResource("SecondaryButton"), Content = $"Mostrar mais ({clusters.Count - _shown})", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12) };
            more.Click += (_, _) =>
            {
                _shown += PageSize;
                RenderList();
            };
            ListPanel.Children.Add(more);
        }
        if (clusters.Count == 0)
        {
            var text = _news.Articles.Count == 0
                ? "Nenhuma notícia ainda. Se a busca falhar, o motivo aparece em \"Fontes e assuntos\"."
                : _filter == "following" && _settings.News.FollowedTopics.Count == 0
                    ? "Você ainda não segue nenhum assunto. Adicione em \"Fontes e assuntos\"."
                    : "Nada encontrado com esses filtros.";
            ListPanel.Children.Add(AgendaUi.Text(text, "CaptionText"));
        }
    }

    // ---------- Cartão ----------

    private Border Card(List<NewsArticle> group)
    {
        var lead = group[0];
        var source = _news.Source(lead.SourceId);
        var topics = TopicKeys;
        var followed = topics.Any(t => NewsTitles.Matches(lead.TitleKey, t));
        var panel = new StackPanel();

        var meta = new List<string> { TrustedSources.Label(source?.Category ?? NewsCategory.Geral), source?.Name ?? "Fonte removida" };
        meta.Add(lead.PublishedAt is { } p ? $"publicado {Relative(p)}" : "sem data de publicação");
        meta.Add($"consultado {Relative(lead.FetchedAt)}");
        var metaText = AgendaUi.Text(string.Join(" · ", meta), "CaptionText", 11.5, followed ? (Brush)FindResource("AccentBrush") : null);
        panel.Children.Add(metaText);

        var title = new TextBlock
        {
            Text = lead.Title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = lead.IsRead ? Res("TextSecondaryBrush") : Res("TextPrimaryBrush"),
        };
        panel.Children.Add(title);
        if (lead.Summary is { } summary)
        {
            var text = new TextBlock { Text = summary, Style = (Style)FindResource("CaptionText"), FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0), MaxHeight = 54, TextTrimming = TextTrimming.WordEllipsis };
            panel.Children.Add(text);
        }
        if (lead.Author is { } author)
        {
            panel.Children.Add(AgendaUi.Text($"Por {author}", "CaptionText", 11.5));
        }

        var others = group.Skip(1).ToList();
        if (others.Count > 0)
        {
            var also = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            also.Children.Add(AgendaUi.Text("Também em:", "CaptionText", 11.5));
            foreach (var other in others.Take(6))
            {
                var name = _news.Source(other.SourceId)?.Name ?? "?";
                var link = new Button { Style = (Style)FindResource("Chip"), Content = name, Margin = new Thickness(6, 0, 0, 0), ToolTip = other.Title };
                AutomationProperties.SetName(link, $"Abrir em {name}: {other.Title}");
                link.Click += (_, _) => Open(other);
                also.Children.Add(link);
            }
            panel.Children.Add(also);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 6, 0, -6) };
        var open = AgendaUi.IconButton("", "Abrir no navegador", (_, _) => Open(lead));
        actions.Children.Add(open);
        actions.Children.Add(Toggle(lead.IsRead ? "" : "", lead.IsRead ? "Marcar como não lida" : "Marcar como lida", false,
            () => _news.SetState(lead.Id, read: !lead.IsRead)));
        actions.Children.Add(Toggle(lead.IsSaved ? "" : "", lead.IsSaved ? "Remover dos salvos" : "Salvar para ler depois", lead.IsSaved,
            () => _news.SetState(lead.Id, saved: !lead.IsSaved)));
        actions.Children.Add(Toggle(lead.IsFavorite ? "" : "", lead.IsFavorite ? "Remover dos favoritos" : "Favoritar", lead.IsFavorite,
            () => _news.SetState(lead.Id, favorite: !lead.IsFavorite)));
        panel.Children.Add(actions);

        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 10), Child = panel, Opacity = lead.IsRead ? 0.75 : 1 };
        AutomationProperties.SetName(card, $"{lead.Title}, {source?.Name}");
        title.Cursor = Cursors.Hand;
        title.MouseLeftButtonUp += (_, _) => Open(lead);
        return card;
    }

    private Button Toggle(string glyph, string name, bool on, Action action)
    {
        var button = AgendaUi.IconButton(glyph, name, (_, _) => action());
        if (on)
        {
            button.Foreground = (Brush)FindResource("AccentBrush");
        }
        return button;
    }

    private void Open(NewsArticle article)
    {
        // Revalida: só http/https vai para o navegador (nunca file:, javascript: etc.).
        if (!NewsUrl.TryParseHttp(article.Url, null, out var url))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }
        if (!article.IsRead)
        {
            _news.SetState(article.Id, read: true);
        }
    }

    // ---------- Fontes e assuntos ----------

    private void RenderSources()
    {
        TopicChips.Children.Clear();
        foreach (var topic in _settings.News.FollowedTopics)
        {
            var chip = new Button { Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 6), ToolTip = "Deixar de seguir" };
            chip.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new TextBlock { Text = topic }, new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Margin = new Thickness(6, 2, 0, 0) } },
            };
            AutomationProperties.SetName(chip, $"Deixar de seguir {topic}");
            chip.Click += (_, _) => _settings.UpdateNews(n => n with { FollowedTopics = [.. n.FollowedTopics.Where(t => t != topic)] });
            TopicChips.Children.Add(chip);
        }

        SourceList.Children.Clear();
        foreach (var group in _news.Sources.GroupBy(s => s.Category))
        {
            var heading = AgendaUi.Text(TrustedSources.Label(group.Key), "CaptionText", 11.5);
            heading.Margin = new Thickness(0, 10, 0, 2);
            SourceList.Children.Add(heading);
            foreach (var source in group)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                if (!source.BuiltIn)
                {
                    var remove = AgendaUi.IconButton("", $"Remover {source.Name}", (_, _) => _news.RemoveSource(source.Id), size: 26);
                    DockPanel.SetDock(remove, Dock.Right);
                    row.Children.Add(remove);
                }
                var check = new CheckBox { IsChecked = source.Enabled, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
                AutomationProperties.SetName(check, $"Mostrar {source.Name}");
                check.Click += (_, _) => _news.SetSourceEnabled(source.Id, check.IsChecked == true);
                row.Children.Add(check);
                var text = new StackPanel();
                text.Children.Add(AgendaUi.Text(source.Name + (source.BuiltIn ? "" : " (sua)"), "BodyText", 13));
                var status = source.LastError is { } error
                    ? AgendaUi.Text($"Erro: {error}{(source.NextFetchAt is { } next ? $" · nova tentativa {Relative(next)}" : "")}", "CaptionText", 11, Res("WarningBrush"))
                    : AgendaUi.Text(source.LastSuccessAt is { } ok ? $"OK {Relative(ok)}" : source.Enabled ? "Ainda não consultada" : "Oculta", "CaptionText", 11);
                status.TextWrapping = TextWrapping.Wrap;
                text.Children.Add(status);
                text.ToolTip = source.FeedUrl;
                row.Children.Add(text);
                SourceList.Children.Add(row);
            }
        }
    }

    private void Sources_Click(object sender, RoutedEventArgs e)
    {
        SourcesPanel.Visibility = SourcesPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SourcesPanel.Visibility == Visibility.Visible)
        {
            RenderSources();
        }
    }

    private void Topic_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddTopic();
            e.Handled = true;
        }
    }

    private void AddTopic_Click(object sender, RoutedEventArgs e) => AddTopic();

    private void AddTopic()
    {
        var topic = TopicBox.Text.Trim();
        if (topic.Length is < 2 or > 60)
        {
            return;
        }
        _settings.UpdateNews(n => n with { FollowedTopics = [.. n.FollowedTopics, topic] });
        TopicBox.Clear();
    }

    private void NotifyTopics_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            _settings.UpdateNews(n => n with { NotifyFollowedTopics = NotifyTopics.IsChecked == true });
        }
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && IntervalBox.SelectedItem is Choice<int> c)
        {
            _settings.UpdateNews(n => n with { RefreshMinutes = c.Value });
        }
    }

    private void Retention_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && RetentionBox.SelectedItem is Choice<int> c)
        {
            _settings.UpdateNews(n => n with { RetentionDays = c.Value });
        }
    }

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        if (NewSourceCategory.SelectedItem is not Choice<NewsCategory> category)
        {
            return;
        }
        AddSourceButton.IsEnabled = false;
        SourceMessage.Visibility = Visibility.Visible;
        SourceMessage.Foreground = Res("TextSecondaryBrush");
        SourceMessage.Text = "Verificando o feed…";
        try
        {
            var error = await _news.AddSourceAsync(NewSourceUrl.Text, category.Value);
            SourceMessage.Text = error ?? "Fonte adicionada.";
            SourceMessage.Foreground = error is null ? Res("SuccessBrush") : Res("DangerBrush");
            if (error is null)
            {
                NewSourceUrl.Clear();
                RenderSources();
            }
        }
        finally
        {
            AddSourceButton.IsEnabled = true;
        }
    }

    // ---------- Ações gerais ----------

    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        _settings.UpdateNews(n => n with { Enabled = true });
        _ = RefreshAsync();
    }

    private void Disable_Click(object sender, RoutedEventArgs e)
    {
        SourcesPanel.Visibility = Visibility.Collapsed;
        _settings.UpdateNews(n => n with { Enabled = false });
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "Atualizando…";
        await _news.RefreshNowAsync();
        Render();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            _shown = PageSize;
            RenderList();
        }
    }

    private void MarkAllRead_Click(object sender, RoutedEventArgs e) =>
        _news.MarkAllRead(Filtered().GroupBy(a => a.ClusterId).Take(_shown).SelectMany(g => g).Select(a => a.Id));

    public void Dispose()
    {
        _searchDelay.Stop();
        _subscriptions.ForEach(s => s.Dispose());
        GC.SuppressFinalize(this);
    }
}
