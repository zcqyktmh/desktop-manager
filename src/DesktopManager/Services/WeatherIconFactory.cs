using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;

namespace DesktopManager.Services;

public sealed record WeatherVisual(Border Container, Grid Icon);

public static class WeatherIconFactory
{
    public static WeatherVisual Create(int code = 3, bool isDay = true)
    {
        var (top, bottom, condition) = Palette(code, isDay);
        var container = new Border
        {
            Width = 68,
            Height = 68,
            CornerRadius = new CornerRadius(24),
            Background = new LinearGradientBrush(top, bottom, 45),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };
        var icon = new Grid { Width = 58, Height = 58 };
        container.Child = icon;
        AddAtmosphere(icon, condition);
        if (code is 0 or 1 or 2 || !isDay)
            AddSun(icon, isDay, code is 1 or 2);
        if (code is not 0 && (code != 1 || !isDay))
            AddCloud(icon, condition is "rain" or "storm" or "snow" or "fog");
        if (condition is "rain" or "storm") AddRain(icon);
        if (condition == "snow") AddSnow(icon);
        if (condition == "storm") AddLightning(icon);
        if (condition == "fog") AddFog(icon);
        container.Tag = condition;
        return new WeatherVisual(container, icon);
    }

    private static (Color Top, Color Bottom, string Condition) Palette(int code, bool isDay)
    {
        if (!isDay && code <= 3) return (Color.FromRgb(44, 67, 126), Color.FromRgb(106, 118, 184), "night");
        return code switch
        {
            0 => (Color.FromRgb(255, 190, 71), Color.FromRgb(255, 224, 123), "sun"),
            1 or 2 => (Color.FromRgb(77, 167, 235), Color.FromRgb(143, 214, 249), "partly"),
            45 or 48 => (Color.FromRgb(144, 169, 184), Color.FromRgb(199, 216, 220), "fog"),
            51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 =>
                (Color.FromRgb(66, 143, 206), Color.FromRgb(151, 207, 237), "rain"),
            71 or 73 or 75 or 77 or 85 or 86 => (Color.FromRgb(112, 176, 221), Color.FromRgb(206, 233, 249), "snow"),
            95 or 96 or 99 => (Color.FromRgb(86, 91, 139), Color.FromRgb(157, 155, 190), "storm"),
            _ => (Color.FromRgb(121, 164, 196), Color.FromRgb(194, 218, 228), "cloud")
        };
    }

    private static void AddAtmosphere(Grid root, string condition)
    {
        if (condition is "sun" or "partly" or "night")
        {
            var glow = new Ellipse { Width = 34, Height = 34, Fill = Brush(Color.FromArgb(75, 255, 255, 220)), Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 7 } };
            root.Children.Add(glow);
        }
    }

    private static void AddSun(Grid root, bool isDay, bool subtle)
    {
        var group = new Grid { Width = 35, Height = 35, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -11, 0, 0) };
        var rotation = new RotateTransform { CenterX = 17.5, CenterY = 17.5 };
        group.RenderTransform = rotation;
        var rayBrush = Brush(isDay ? Color.FromRgb(255, 239, 150) : Color.FromRgb(231, 232, 255));
        for (var index = 0; index < 8; index++)
        {
            var ray = new RoundedRectangle { Width = 3, Height = 7, RadiusX = 1.5, RadiusY = 1.5, Fill = rayBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            ray.RenderTransform = new RotateTransform(index * 45, 1.5, 17.5);
            group.Children.Add(ray);
        }
        var sun = new Ellipse { Width = 20, Height = 20, Fill = new RadialGradientBrush(Color.FromRgb(255, 255, 200), Color.FromRgb(255, 177, 57)), Stroke = Brush(Color.FromArgb(190, 255, 246, 179)), StrokeThickness = 1 };
        sun.HorizontalAlignment = HorizontalAlignment.Center;
        sun.VerticalAlignment = VerticalAlignment.Center;
        group.Children.Add(sun);
        root.Children.Add(group);
        var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(36))) { RepeatBehavior = RepeatBehavior.Forever };
        rotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        if (subtle)
        {
            var bob = new DoubleAnimation(-11, -8, new Duration(TimeSpan.FromSeconds(2.4))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            group.BeginAnimation(FrameworkElement.MarginProperty, bob);
        }
    }

    private static void AddCloud(Grid root, bool foreground)
    {
        var cloud = new Grid { Width = 47, Height = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, foreground ? 10 : 15, 0, 0) };
        var fill = new LinearGradientBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(206, 232, 245), 90);
        cloud.Children.Add(new Ellipse { Width = 23, Height = 18, Fill = fill, Margin = new Thickness(2, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
        cloud.Children.Add(new Ellipse { Width = 25, Height = 25, Fill = fill, Margin = new Thickness(12, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
        cloud.Children.Add(new Ellipse { Width = 23, Height = 18, Fill = fill, Margin = new Thickness(0, 8, 1, 0), HorizontalAlignment = HorizontalAlignment.Right });
        var baseCloud = new Border { Width = 39, Height = 15, CornerRadius = new CornerRadius(8), Background = fill, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
        cloud.Children.Add(baseCloud);
        root.Children.Add(cloud);
        var travel = new DoubleAnimation(-1.5, 1.5, new Duration(TimeSpan.FromSeconds(3.2))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        cloud.RenderTransform = new TranslateTransform();
        ((TranslateTransform)cloud.RenderTransform).BeginAnimation(TranslateTransform.XProperty, travel);
    }

    private static void AddRain(Grid root)
    {
        for (var i = 0; i < 3; i++)
        {
            var drop = new ShapePath
            {
                Data = new EllipseGeometry(new Point(2, 4), 1.6, 3.5),
                Fill = Brush(Color.FromRgb(102, 211, 255)),
                Width = 5,
                Height = 9,
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-17 + i * 17, 26, 0, 0),
                Opacity = .9
            };
            root.Children.Add(drop);
            var move = new DoubleAnimation(-4, 10, new Duration(TimeSpan.FromSeconds(.72))) { BeginTime = TimeSpan.FromMilliseconds(i * 190), RepeatBehavior = RepeatBehavior.Forever };
            var fade = new DoubleAnimation(.95, .1, new Duration(TimeSpan.FromSeconds(.72))) { BeginTime = TimeSpan.FromMilliseconds(i * 190), RepeatBehavior = RepeatBehavior.Forever };
            drop.RenderTransform = new TranslateTransform();
            ((TranslateTransform)drop.RenderTransform).BeginAnimation(TranslateTransform.YProperty, move);
            drop.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    private static void AddSnow(Grid root)
    {
        for (var i = 0; i < 4; i++)
        {
            var flake = new TextBlock { Text = "✦", FontSize = 8 + i % 2 * 2, Foreground = Brush(Color.FromRgb(255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-18 + i * 12, 24, 0, 0) };
            root.Children.Add(flake);
            var move = new DoubleAnimation(-3, 14, new Duration(TimeSpan.FromSeconds(1.8 + i * .2))) { BeginTime = TimeSpan.FromMilliseconds(i * 210), RepeatBehavior = RepeatBehavior.Forever };
            flake.RenderTransform = new TranslateTransform();
            ((TranslateTransform)flake.RenderTransform).BeginAnimation(TranslateTransform.YProperty, move);
        }
    }

    private static void AddLightning(Grid root)
    {
        var bolt = new ShapePath
        {
            Data = Geometry.Parse("M 0,0 L 8,0 L 4,7 L 10,7 L 0,19 L 3,10 L 0,10 Z"),
            Fill = Brush(Color.FromRgb(255, 229, 94)),
            Width = 12,
            Height = 22,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(17, 23, 0, 0)
        };
        root.Children.Add(bolt);
        var blink = new DoubleAnimation(.35, 1, new Duration(TimeSpan.FromSeconds(.6))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        bolt.BeginAnimation(UIElement.OpacityProperty, blink);
    }

    private static void AddFog(Grid root)
    {
        for (var i = 0; i < 3; i++)
        {
            var line = new Border { Width = 39, Height = 2.5, CornerRadius = new CornerRadius(2), Background = Brush(Color.FromArgb(225, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8 + i * 6, 0, 0) };
            root.Children.Add(line);
            var shift = new DoubleAnimation(-4, 4, new Duration(TimeSpan.FromSeconds(2.3 + i * .25))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            line.RenderTransform = new TranslateTransform();
            ((TranslateTransform)line.RenderTransform).BeginAnimation(TranslateTransform.XProperty, shift);
        }
    }

    private static SolidColorBrush Brush(Color color) => new(color);

    private sealed class RoundedRectangle : Shape
    {
        public double RadiusX { get; set; }
        public double RadiusY { get; set; }
        protected override Geometry DefiningGeometry => new RectangleGeometry(new Rect(0, 0, RenderSize.Width, RenderSize.Height), RadiusX, RadiusY);
    }
}
