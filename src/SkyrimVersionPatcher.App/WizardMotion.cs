using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SkyrimVersionPatcher.App;

/// <summary>Small render-only transitions; navigation and control values change immediately.</summary>
public static class WizardMotion
{
    private static readonly TimeSpan FeedbackDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan PageDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ResultDuration = TimeSpan.FromMilliseconds(280);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(WizardMotion),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, MotionEnabledChanged));

    public static readonly DependencyProperty OpacityTargetProperty = RegisterTarget("OpacityTarget");
    public static readonly DependencyProperty ScaleTargetProperty = RegisterTarget("ScaleTarget");
    public static readonly DependencyProperty AngleTargetProperty = RegisterTarget("AngleTarget");
    public static readonly DependencyProperty RevealOnVisibleProperty = DependencyProperty.RegisterAttached(
        "RevealOnVisible", typeof(bool), typeof(WizardMotion), new PropertyMetadata(false, RevealChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(MotionState), typeof(WizardMotion));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static double GetOpacityTarget(DependencyObject element) => (double)element.GetValue(OpacityTargetProperty);
    public static void SetOpacityTarget(DependencyObject element, double value) => element.SetValue(OpacityTargetProperty, value);
    public static double GetScaleTarget(DependencyObject element) => (double)element.GetValue(ScaleTargetProperty);
    public static void SetScaleTarget(DependencyObject element, double value) => element.SetValue(ScaleTargetProperty, value);
    public static double GetAngleTarget(DependencyObject element) => (double)element.GetValue(AngleTargetProperty);
    public static void SetAngleTarget(DependencyObject element, double value) => element.SetValue(AngleTargetProperty, value);
    public static bool GetRevealOnVisible(DependencyObject element) => (bool)element.GetValue(RevealOnVisibleProperty);
    public static void SetRevealOnVisible(DependencyObject element, bool value) => element.SetValue(RevealOnVisibleProperty, value);

    private static DependencyProperty RegisterTarget(string name) => DependencyProperty.RegisterAttached(
        name, typeof(double), typeof(WizardMotion), new PropertyMetadata(double.NaN, TargetChanged));

    public static void Enter(FrameworkElement element, double horizontalOffset = 0, double verticalOffset = 0)
    {
        var state = GetState(element);
        state.Stop();
        var translate = GetTransform<TranslateTransform>(element);
        Animate(state, element, UIElement.OpacityProperty, 1, PageDuration, from: 0.35);
        Animate(state, translate, TranslateTransform.XProperty, 0, PageDuration, from: horizontalOffset);
        Animate(state, translate, TranslateTransform.YProperty, 0, PageDuration, from: verticalOffset);
    }

    public static void RevealResult(FrameworkElement element)
    {
        var state = GetState(element);
        var scale = GetTransform<ScaleTransform>(element);
        Animate(state, scale, ScaleTransform.ScaleXProperty, 1, ResultDuration, from: 0.88);
        Animate(state, scale, ScaleTransform.ScaleYProperty, 1, ResultDuration, from: 0.88);
    }

    private static void TargetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element) ApplyTargets(GetState(element), changedProperty: args.Property);
    }

    private static void MotionEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender.GetValue(StateProperty) is MotionState state && !(bool)args.NewValue) state.Stop();
    }

    private static void RevealChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element && (bool)args.NewValue) GetState(element);
    }

    private static MotionState GetState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is MotionState existing) return existing;
        var state = new MotionState(element);
        element.SetValue(StateProperty, state);
        // XAML can assign RenderTransform after its attached target values.
        element.Loaded += (_, _) => ApplyTargets(state, animate: false);
        element.Unloaded += (_, _) => state.Stop();
        element.IsEnabledChanged += (_, _) => { if (!element.IsEnabled) state.Stop(); };
        element.IsVisibleChanged += (_, _) =>
        {
            if (!element.IsVisible) state.Stop();
            else if (GetRevealOnVisible(element)) Enter(element, verticalOffset: 8);
        };
        return state;
    }

    private static void ApplyTargets(MotionState state, bool animate = true, DependencyProperty? changedProperty = null)
    {
        var element = state.Element;
        if ((changedProperty is null || changedProperty == OpacityTargetProperty)
            && GetOpacityTarget(element) is var opacity && !double.IsNaN(opacity))
            Animate(state, element, UIElement.OpacityProperty, opacity, FeedbackDuration, animate: animate);
        if ((changedProperty is null || changedProperty == ScaleTargetProperty)
            && GetScaleTarget(element) is var size && !double.IsNaN(size))
        {
            var scale = GetTransform<ScaleTransform>(element);
            Animate(state, scale, ScaleTransform.ScaleXProperty, size, FeedbackDuration, animate: animate);
            Animate(state, scale, ScaleTransform.ScaleYProperty, size, FeedbackDuration, animate: animate);
        }
        if ((changedProperty is null || changedProperty == AngleTargetProperty)
            && GetAngleTarget(element) is var angle && !double.IsNaN(angle))
            Animate(state, GetTransform<RotateTransform>(element), RotateTransform.AngleProperty, angle, FeedbackDuration, animate: animate);
    }

    private static T GetTransform<T>(FrameworkElement element) where T : Transform, new()
    {
        // Reuse the mutable clone, so scales never accumulate across template states.
        if (element.RenderTransform.IsFrozen) element.RenderTransform = element.RenderTransform.Clone();
        if (element.RenderTransform is T existing) return existing;
        if (element.RenderTransform is TransformGroup group)
        {
            if (group.Children.OfType<T>().FirstOrDefault() is { } child)
            {
                if (!child.IsFrozen) return child;
                var clone = (T)child.Clone();
                group.Children[group.Children.IndexOf(child)] = clone;
                return clone;
            }
            var added = new T();
            group.Children.Add(added);
            return added;
        }
        var transform = new T();
        // Preserve a different transform supplied by the template.
        element.RenderTransform = new TransformGroup { Children = { element.RenderTransform, transform } };
        return transform;
    }

    private static void Animate(MotionState state, DependencyObject target, DependencyProperty property,
        double to, TimeSpan duration, double? from = null, bool animate = true)
    {
        var current = from ?? (double)target.GetValue(property);
        state.Track(target, property);
        BeginAnimation(target, property, null);
        // These visual properties are owned by the motion target. A durable base
        // value is required when WPF invalidates/removes the animation clock.
        target.SetValue(property, to);
        if (!animate || !GetIsEnabled(state.Element) || !state.Element.IsEnabled || !state.Element.IsLoaded || !state.Element.IsVisible || current == to) return;
        var animation = new DoubleAnimation(current, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        // A completed clock must never clear a newer transition after rapid input.
        var revision = state.NextRevision(target, property);
        animation.Completed += (_, _) =>
        {
            if (state.IsCurrent(target, property, revision)) BeginAnimation(target, property, null);
        };
        BeginAnimation(target, property, animation);
    }

    private static void BeginAnimation(DependencyObject target, DependencyProperty property, AnimationTimeline? animation)
    {
        if (target is Animatable animatable) animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        else if (target is UIElement element) element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private sealed class MotionState(FrameworkElement element)
    {
        public FrameworkElement Element { get; } = element;
        private readonly Dictionary<(DependencyObject Target, DependencyProperty Property), int> revisions = [];

        public void Track(DependencyObject target, DependencyProperty property) => revisions.TryAdd((target, property), 0);
        public int NextRevision(DependencyObject target, DependencyProperty property) => ++revisions[(target, property)];
        public bool IsCurrent(DependencyObject target, DependencyProperty property, int revision) => revisions[(target, property)] == revision;

        public void Stop()
        {
            foreach (var key in revisions.Keys.ToArray())
            {
                revisions[key]++;
                BeginAnimation(key.Target, key.Property, null);
            }
        }
    }
}
