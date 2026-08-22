using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WortBruecke.App.Infrastructure;

/// <summary>
/// Supplies the restrained transform-only feedback used by the visual system.
/// Every transition is disabled when Windows animation effects are disabled or
/// High Contrast is active; control state still changes immediately.
/// </summary>
public static class MicroInteraction
{
    private const double HoverOffset = -1.0;
    private const double PressedOffset = 0.6;
    private const double ToggleTravel = 20.0;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(MicroInteraction),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty IsToggleSwitchProperty = DependencyProperty.RegisterAttached(
        "IsToggleSwitch",
        typeof(bool),
        typeof(MicroInteraction),
        new PropertyMetadata(false, OnIsToggleSwitchChanged));

    private static readonly DependencyProperty IsAttachedProperty = DependencyProperty.RegisterAttached(
        "IsAttached",
        typeof(bool),
        typeof(MicroInteraction),
        new PropertyMetadata(false));

    private static readonly DependencyProperty IsToggleAttachedProperty = DependencyProperty.RegisterAttached(
        "IsToggleAttached",
        typeof(bool),
        typeof(MicroInteraction),
        new PropertyMetadata(false));

    private static readonly DependencyProperty TranslateProperty = DependencyProperty.RegisterAttached(
        "Translate",
        typeof(TranslateTransform),
        typeof(MicroInteraction),
        new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsToggleSwitch(DependencyObject element) => (bool)element.GetValue(IsToggleSwitchProperty);

    public static void SetIsToggleSwitch(DependencyObject element, bool value) => element.SetValue(IsToggleSwitchProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            Attach(element);
        }
        else
        {
            Detach(element);
        }
    }

    private static void Attach(FrameworkElement element)
    {
        if ((bool)element.GetValue(IsAttachedProperty))
        {
            return;
        }

        element.SetValue(IsAttachedProperty, true);
        element.MouseEnter += OnMouseEnter;
        element.MouseLeave += OnMouseLeave;
        element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        element.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        element.PreviewKeyDown += OnPreviewKeyDown;
        element.PreviewKeyUp += OnPreviewKeyUp;
        element.IsEnabledChanged += OnElementIsEnabledChanged;
    }

    private static void Detach(FrameworkElement element)
    {
        if (!(bool)element.GetValue(IsAttachedProperty))
        {
            return;
        }

        element.MouseEnter -= OnMouseEnter;
        element.MouseLeave -= OnMouseLeave;
        element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        element.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
        element.PreviewKeyDown -= OnPreviewKeyDown;
        element.PreviewKeyUp -= OnPreviewKeyUp;
        element.IsEnabledChanged -= OnElementIsEnabledChanged;
        element.SetValue(IsAttachedProperty, false);
        AnimateTo(element, 0, 80);
    }

    private static void OnMouseEnter(object sender, MouseEventArgs args)
    {
        if (sender is FrameworkElement { IsEnabled: true } element)
        {
            AnimateTo(element, HoverOffset, 120);
        }
    }

    private static void OnMouseLeave(object sender, MouseEventArgs args)
    {
        if (sender is FrameworkElement element)
        {
            AnimateTo(element, 0, 80);
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (sender is FrameworkElement { IsEnabled: true } element)
        {
            AnimateTo(element, PressedOffset, 80);
        }
    }

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (sender is FrameworkElement { IsEnabled: true } element)
        {
            AnimateTo(element, element.IsMouseOver ? HoverOffset : 0, 80);
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (sender is FrameworkElement { IsEnabled: true } element && args.Key is Key.Enter or Key.Space)
        {
            AnimateTo(element, PressedOffset, 80);
        }
    }

    private static void OnPreviewKeyUp(object sender, KeyEventArgs args)
    {
        if (sender is FrameworkElement { IsEnabled: true } element && args.Key is Key.Enter or Key.Space)
        {
            AnimateTo(element, 0, 80);
        }
    }

    private static void OnElementIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element && !(bool)args.NewValue)
        {
            AnimateTo(element, 0, 80);
        }
    }

    private static void AnimateTo(FrameworkElement element, double offset, int milliseconds)
    {
        var translate = EnsureTranslate(element);
        if (!ShouldAnimate)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = offset;
            return;
        }

        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation
            {
                To = offset,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static TranslateTransform EnsureTranslate(FrameworkElement element)
    {
        if (element.GetValue(TranslateProperty) is TranslateTransform existing)
        {
            return existing;
        }

        var translate = new TranslateTransform();
        Transform newTransform;
        if (element.RenderTransform is null || element.RenderTransform.Value.IsIdentity)
        {
            newTransform = translate;
        }
        else
        {
            var group = new TransformGroup();
            group.Children.Add(element.RenderTransform.CloneCurrentValue());
            group.Children.Add(translate);
            newTransform = group;
        }

        element.SetCurrentValue(UIElement.RenderTransformProperty, newTransform);
        element.SetValue(TranslateProperty, translate);
        return translate;
    }

    private static void OnIsToggleSwitchChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ToggleButton toggle)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            AttachToggle(toggle);
        }
        else
        {
            DetachToggle(toggle);
        }
    }

    private static void AttachToggle(ToggleButton toggle)
    {
        if ((bool)toggle.GetValue(IsToggleAttachedProperty))
        {
            return;
        }

        toggle.SetValue(IsToggleAttachedProperty, true);
        toggle.Loaded += OnToggleStateChanged;
        toggle.Checked += OnToggleStateChanged;
        toggle.Unchecked += OnToggleStateChanged;
        toggle.Indeterminate += OnToggleStateChanged;
    }

    private static void DetachToggle(ToggleButton toggle)
    {
        if (!(bool)toggle.GetValue(IsToggleAttachedProperty))
        {
            return;
        }

        toggle.Loaded -= OnToggleStateChanged;
        toggle.Checked -= OnToggleStateChanged;
        toggle.Unchecked -= OnToggleStateChanged;
        toggle.Indeterminate -= OnToggleStateChanged;
        toggle.SetValue(IsToggleAttachedProperty, false);
    }

    private static void OnToggleStateChanged(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton toggle)
        {
            return;
        }

        toggle.ApplyTemplate();
        if (toggle.Template.FindName("PART_SwitchKnob", toggle) is not FrameworkElement knob)
        {
            return;
        }

        var translate = knob.RenderTransform as TranslateTransform;
        if (translate is null || translate.IsFrozen)
        {
            translate = translate?.CloneCurrentValue() ?? new TranslateTransform();
            knob.SetCurrentValue(UIElement.RenderTransformProperty, translate);
        }

        var target = toggle.IsChecked == true ? ToggleTravel : 0;
        if (!ShouldAnimate || !toggle.IsLoaded)
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = target;
            return;
        }

        translate.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static bool ShouldAnimate => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
}
