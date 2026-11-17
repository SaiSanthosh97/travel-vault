using System.Windows.Input;
using TravelVault.App.Resources.Strings;
using TravelVault.App.Services;

namespace TravelVault.App.Views;

/// <summary>One document in a list: icon, title, when, details, and a status badge (colour always with text).</summary>
public sealed class DocumentRowView : Border
{
    public static readonly BindableProperty RowProperty =
        BindableProperty.Create(nameof(Row), typeof(DocumentRow), typeof(DocumentRowView), propertyChanged: (b, _, n) => ((DocumentRowView)b).Show(n as DocumentRow));

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(DocumentRowView));

    private readonly Label _icon = new() { FontSize = 28, VerticalOptions = LayoutOptions.Center };
    private readonly Label _title = new() { FontFamily = "OpenSansSemibold", FontSize = 19 };
    private readonly Label _when = new() { FontSize = 17 };
    private readonly Label _subtitle = new();
    private readonly Label _badgeText = new() { FontSize = 14, TextColor = Colors.White, FontFamily = "OpenSansSemibold" };
    private readonly Border _badge;

    public DocumentRowView()
    {
        Style = (Style)Application.Current!.Resources["Card"];
        Padding = new Thickness(14, 10);
        _subtitle.Style = (Style)Application.Current.Resources["Subtle"];
        _badge = new Border { Style = (Style)Application.Current.Resources["Badge"], Content = _badgeText, IsVisible = false, HorizontalOptions = LayoutOptions.Start };
        var text = new VerticalStackLayout { Spacing = 2, Children = { _title, _when, _subtitle, _badge } };
        var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 12 };
        grid.Add(_icon, 0);
        grid.Add(text, 1);
        Content = grid;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (Row is { } row && Command?.CanExecute(row) == true)
            {
                Command.Execute(row);
            }
        };
        GestureRecognizers.Add(tap);
    }

    public DocumentRow? Row
    {
        get => (DocumentRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private void Show(DocumentRow? row)
    {
        if (row is null)
        {
            return;
        }

        _icon.Text = row.Icon;
        _title.Text = row.IsPrivate ? $"🔒 {row.Title}" : row.Title;
        _when.Text = row.When;
        _when.IsVisible = row.When.Length > 0;
        _subtitle.Text = row.Subtitle;
        _subtitle.IsVisible = row.Subtitle.Length > 0;
        _badge.IsVisible = row.Badge is not null;
        _badgeText.Text = row.Badge;
        _badge.BackgroundColor = row.BadgeColor ?? Colors.Gray;
        SemanticProperties.SetDescription(this, $"{row.Title}. {row.When}. {row.Subtitle}. {row.Badge}{(row.IsPrivate ? ". " + AppResources.Document_Private : "")}");
    }
}
