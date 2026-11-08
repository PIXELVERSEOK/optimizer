using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EvoOptimizer.App.Views;

public partial class EvoStandardPage : UserControl
{
    public static readonly DependencyProperty PageTitleProperty =
        DependencyProperty.Register(nameof(PageTitle), typeof(string), typeof(EvoStandardPage),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(EvoStandardPage),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionBarContentProperty =
        DependencyProperty.Register(nameof(ActionBarContent), typeof(object), typeof(EvoStandardPage));

    public static readonly DependencyProperty PageContentProperty =
        DependencyProperty.Register(nameof(PageContent), typeof(object), typeof(EvoStandardPage));

    public static readonly DependencyProperty ContextualSidePanelContentProperty =
        DependencyProperty.Register(nameof(ContextualSidePanelContent), typeof(object), typeof(EvoStandardPage));

    public static readonly DependencyProperty ModuleStatusProperty =
        DependencyProperty.Register(nameof(ModuleStatus), typeof(string), typeof(EvoStandardPage));

    public static readonly DependencyProperty ModuleStatusDetailsProperty =
        DependencyProperty.Register(nameof(ModuleStatusDetails), typeof(string), typeof(EvoStandardPage));

    public static readonly DependencyProperty ModuleComponentProperty =
        DependencyProperty.Register(nameof(ModuleComponent), typeof(string), typeof(EvoStandardPage));

    public static readonly DependencyProperty ModuleStatusBrushProperty =
        DependencyProperty.Register(nameof(ModuleStatusBrush), typeof(Brush), typeof(EvoStandardPage));

    public static readonly DependencyProperty IsContextualSidePanelOpenProperty =
        DependencyProperty.Register(
            nameof(IsContextualSidePanelOpen),
            typeof(bool),
            typeof(EvoStandardPage),
            new PropertyMetadata(false, OnIsContextualSidePanelOpenChanged));

    public EvoStandardPage()
    {
        InitializeComponent();
    }

    public string PageTitle
    {
        get => (string)GetValue(PageTitleProperty);
        set => SetValue(PageTitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? ActionBarContent
    {
        get => GetValue(ActionBarContentProperty);
        set => SetValue(ActionBarContentProperty, value);
    }

    public object? PageContent
    {
        get => GetValue(PageContentProperty);
        set => SetValue(PageContentProperty, value);
    }

    public object? ContextualSidePanelContent
    {
        get => GetValue(ContextualSidePanelContentProperty);
        set => SetValue(ContextualSidePanelContentProperty, value);
    }

    public string? ModuleStatus
    {
        get => (string?)GetValue(ModuleStatusProperty);
        set => SetValue(ModuleStatusProperty, value);
    }

    public string? ModuleStatusDetails
    {
        get => (string?)GetValue(ModuleStatusDetailsProperty);
        set => SetValue(ModuleStatusDetailsProperty, value);
    }

    public string? ModuleComponent
    {
        get => (string?)GetValue(ModuleComponentProperty);
        set => SetValue(ModuleComponentProperty, value);
    }

    public Brush? ModuleStatusBrush
    {
        get => (Brush?)GetValue(ModuleStatusBrushProperty);
        set => SetValue(ModuleStatusBrushProperty, value);
    }

    public bool IsContextualSidePanelOpen
    {
        get => (bool)GetValue(IsContextualSidePanelOpenProperty);
        set => SetValue(IsContextualSidePanelOpenProperty, value);
    }

    private static void OnIsContextualSidePanelOpenChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not EvoStandardPage page || !page.IsInitialized)
            return;

        if ((bool)e.NewValue)
        {
            page.ContextualSidePanel.Visibility = Visibility.Visible;
            page.ContextPanelTransform.BeginAnimation(
                System.Windows.Media.TranslateTransform.XProperty,
                new DoubleAnimation(360, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            return;
        }

        var animation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(150));
        animation.Completed += (_, _) =>
        {
            if (!page.IsContextualSidePanelOpen)
                page.ContextualSidePanel.Visibility = Visibility.Collapsed;
        };
        page.ContextPanelTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            animation);
    }
}
