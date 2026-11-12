using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SifuMovesetEditor;

/// <summary>
/// Modal feature guide. The sidebar is rebuilt from <see cref="HelpContent"/> whenever
/// the search box changes; the article is rendered in code because each guide is a
/// small mixed layout (numbered steps, tip callout, location line) that would need
/// several bindings and converters to express in XAML. <see cref="HelpTopic.TutorialId"/>
/// is carried through untouched for the upcoming interactive walkthrough launcher.
/// </summary>
public partial class HelpDialog : Window
{
    private static readonly Brush AccentBrush = MakeBrush(0x89, 0xb4, 0xfa);
    private static readonly Brush TextBrush = MakeBrush(0xcd, 0xd6, 0xf4);
    private static readonly Brush MutedBrush = MakeBrush(0x6c, 0x70, 0x86);
    private static readonly Brush BadgeBrush = MakeBrush(0x31, 0x32, 0x44);
    private static readonly Brush PanelBrush = MakeBrush(0x18, 0x18, 0x25);
    private static readonly Brush BandBrush = MakeBrush(0x11, 0x11, 0x1b);
    private static readonly Brush TipBrush = MakeBrush(0xf9, 0xe2, 0xaf);
    private static readonly Brush LocationBrush = MakeBrush(0xa6, 0xe3, 0xa1);

    private HelpTopic? _current;
    private bool _ready;
    private bool _buildingSidebar;
    private string? _pendingInitialTopicId;

    /// <summary>Set when the user clicks a walkthrough button; the owner runs it and reopens Help.</summary>
    public string? PendingTutorialId { get; private set; }

    /// <summary>Article to reopen after the walkthrough finishes.</summary>
    public string? PendingTutorialTopicId { get; private set; }

    public HelpDialog(string? initialTopicId = null)
    {
        InitializeComponent();

        _pendingInitialTopicId = initialTopicId;
        txtFooter.Text = $"{HelpContent.Topics.Count} guides · {HelpContent.Categories.Count} categories";
        _ready = true;
        BuildSidebar(null);
    }

    private void HelpDialog_Loaded(object sender, RoutedEventArgs e)
    {
        txtSearch.Focus();
    }

    private void HelpDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            txtSearch.Focus();
            txtSearch.SelectAll();
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        searchHint.Visibility = string.IsNullOrEmpty(txtSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        BuildSidebar(txtSearch.Text);
    }

    private void Topic_Click(object sender, RoutedEventArgs e)
    {
        if (_buildingSidebar) return;
        if (sender is RadioButton { Tag: HelpTopic topic }) ShowTopic(topic);
    }

    /// <summary>
    /// Rebuilds the category-grouped sidebar for the current query. Keeps the open
    /// topic when it still matches; otherwise selects the first result. A query with no
    /// results leaves the sidebar empty and swaps the article for a no-match message.
    /// </summary>
    private void BuildSidebar(string? query)
    {
        var matches = HelpContent.Search(query);
        var matchIds = new HashSet<string>(matches.Select(t => t.Id), StringComparer.Ordinal);
        var target = matches.Count == 0 ? null : PickTarget(matches, matchIds);
        _pendingInitialTopicId = null;

        _buildingSidebar = true;
        sidebarPanel.Children.Clear();
        foreach (var category in HelpContent.Categories)
        {
            var topics = category.Topics.Where(t => matchIds.Contains(t.Id)).ToList();
            if (topics.Count == 0) continue;

            sidebarPanel.Children.Add(new Border
            {
                Background = BandBrush,
                BorderBrush = AccentBrush,
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(0, 3, 3, 0),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 14, 0, 6),
                Child = new TextBlock
                {
                    Text = category.Name.ToUpperInvariant(),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = AccentBrush
                }
            });

            foreach (var topic in topics)
            {
                var item = new RadioButton
                {
                    Content = topic.Title,
                    Tag = topic,
                    GroupName = "HelpTopics",
                    Style = (Style)FindResource("TopicRadio"),
                    Margin = new Thickness(7, 0, 0, 0),
                    IsChecked = target != null
                        && string.Equals(topic.Id, target.Id, StringComparison.Ordinal)
                };
                item.Click += Topic_Click;
                sidebarPanel.Children.Add(item);
            }
        }
        _buildingSidebar = false;
        sidebarScroll.ScrollToTop();

        if (target == null)
        {
            ShowEmpty(query);
            return;
        }
        ShowTopic(target);
    }

    /// <summary>
    /// Chooses the article to show: keep the open one when it still matches, otherwise
    /// land on the requested reopening topic, otherwise the first result.
    /// </summary>
    private HelpTopic? PickTarget(IReadOnlyList<HelpTopic> matches, HashSet<string> matchIds)
    {
        if (_current != null && matchIds.Contains(_current.Id)) return _current;
        if (_pendingInitialTopicId != null)
        {
            var requested = matches.FirstOrDefault(t =>
                string.Equals(t.Id, _pendingInitialTopicId, StringComparison.Ordinal));
            if (requested != null) return requested;
        }
        return matches[0];
    }

    private void ShowEmpty(string? query)
    {
        articlePanel.Children.Clear();
        articleScroll.Visibility = Visibility.Collapsed;
        emptyPanel.Visibility = Visibility.Visible;
        emptyHint.Text = string.IsNullOrWhiteSpace(query)
            ? "Try a shorter word or a feature name."
            : $"Nothing matches \"{query}\". Try a shorter word or a feature name.";
    }

    private void ShowTopic(HelpTopic topic)
    {
        _current = topic;
        emptyPanel.Visibility = Visibility.Collapsed;
        articleScroll.Visibility = Visibility.Visible;
        articlePanel.Children.Clear();

        articlePanel.Children.Add(new TextBlock
        {
            Text = topic.Category.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 0, 0, 5)
        });

        articlePanel.Children.Add(new TextBlock
        {
            Text = topic.Title,
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = AccentBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 7)
        });

        articlePanel.Children.Add(new TextBlock
        {
            Text = topic.Summary,
            FontSize = 12.5,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        if (topic.TutorialId != null)
            articlePanel.Children.Add(BuildTutorialCard(topic));

        if (topic.Steps.Count > 0)
        {
            AddSectionLabel("STEPS");
            foreach (var step in topic.Steps) articlePanel.Children.Add(BuildStepRow(step));
        }

        if (topic.HasTips)
        {
            var tipStack = new StackPanel();
            tipStack.Children.Add(new TextBlock
            {
                Text = "TIPS",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = TipBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            foreach (var tip in topic.Tips)
            {
                tipStack.Children.Add(new TextBlock
                {
                    Text = "•  " + tip,
                    FontSize = 12,
                    Foreground = TextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0)
                });
            }
            articlePanel.Children.Add(new Border
            {
                Background = PanelBrush,
                BorderBrush = TipBrush,
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(0, 4, 4, 0),
                Padding = new Thickness(12, 10, 14, 10),
                Margin = new Thickness(0, 12, 0, 4),
                Child = tipStack
            });
        }

        if (topic.HasWhereToFind)
        {
            AddSectionLabel("WHERE TO FIND IT");
            articlePanel.Children.Add(new TextBlock
            {
                Text = "→  " + topic.WhereToFind,
                FontSize = 12,
                Foreground = LocationBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        articleScroll.ScrollToTop();
    }

    /// <summary>
    /// Callout for guides that have an interactive walkthrough. Closing the dialog with a
    /// pending id tells MainWindow to run the scenario and reopen Help afterwards.
    /// </summary>
    private Border BuildTutorialCard(HelpTopic topic)
    {
        var body = new StackPanel();

        body.Children.Add(new TextBlock
        {
            Text = "INTERACTIVE WALKTHROUGH",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = AccentBrush
        });

        body.Children.Add(new TextBlock
        {
            Text = "Practice this feature on a temporary vanilla project. Your work is saved "
                 + "first and put back when you close Help.",
            FontSize = 11.5,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10)
        });

        var start = new Button
        {
            Content = "▶  Start walkthrough",
            Style = (Style)FindResource("AccentButton"),
            Tag = topic,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        start.Click += StartTutorial_Click;
        body.Children.Add(start);

        return new Border
        {
            Background = PanelBrush,
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 4, 4, 0),
            Padding = new Thickness(14, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 4),
            Child = body
        };
    }

    private void StartTutorial_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HelpTopic topic } || topic.TutorialId == null) return;

        PendingTutorialId = topic.TutorialId;
        PendingTutorialTopicId = topic.Id;
        Close();
    }

    private void AddSectionLabel(string text)
    {
        articlePanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 14, 0, 8)
        });
    }

    private static Grid BuildStepRow(HelpStep step)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = BadgeBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = step.Number,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(badge, 0);
        row.Children.Add(badge);

        var text = new TextBlock
        {
            Text = step.Text,
            FontSize = 12.5,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
