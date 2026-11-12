using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace BrimDeck;

// The island's shadow is drawn from a cached picture (ShadowHost's BitmapCache). WPF does not keep an effect's output: whenever
// anything drawn over the shadow changes (the scrolling title, the equalizer, a carousel, the readings), it draws the shadow's
// subtree again and blurs it again, on every frame of that motion. The cache keeps the blurred picture, so those frames only
// compose it. While the island changes size the picture would be made again on every frame, which costs more than blurring
// directly, so the cache is dropped then and comes back once the size has held still for a moment. The same holds while what
// the picture shows changes in place (the surface color, the corners), and while the shadow is not shown at all there is
// nothing to keep. The shadow fades on ShadowHost, outside the cache, so a fade only composes the kept picture.
public partial class MainWindow
{
    private BitmapCache? _shadowCache;
    private DispatcherTimer? _shadowSettle;

    private void InitializeShadow()
    {
        _shadowCache = ShadowHost.CacheMode as BitmapCache;
        _shadowSettle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _shadowSettle.Tick += (_, _) => { _shadowSettle.Stop(); if (ShadowHost.Opacity > 0) ShadowHost.CacheMode = _shadowCache; };
        // The host's size follows the island's margin as well (the island sinks into place as the panel closes).
        IslandShadow.SizeChanged += (_, _) => { UpdateShadowMask(); ShadowChanging(); }; ShadowHost.SizeChanged += (_, _) => ShadowChanging();
        // The color, corners and shoulders the shadow takes from the island; Changed is raised on each frame of a color animation.
        _surface.Changed += (_, _) => ShadowChanging();
        var corners = DependencyPropertyDescriptor.FromProperty(Border.CornerRadiusProperty, typeof(IslandBorder));
        var shoulders = DependencyPropertyDescriptor.FromProperty(IslandBorder.ShoulderRadiusProperty, typeof(IslandBorder));
        var opacity = DependencyPropertyDescriptor.FromProperty(OpacityProperty, typeof(Grid));
        void ShapeChanged(object? sender, EventArgs e) { UpdateShadowMask(); ShadowChanging(); }
        corners.AddValueChanged(Island, ShapeChanged);
        shoulders.AddValueChanged(Island, ShapeChanged);
        // Hidden (the open panel, the line), the picture is let go; shown again, it is kept at once unless a change is under way.
        void OpacityChanged(object? sender, EventArgs e)
        {
            if (ShadowHost.Opacity <= 0) ShadowHost.CacheMode = null;
            else if (ShadowHost.CacheMode is null && !_shadowSettle.IsEnabled) ShadowHost.CacheMode = _shadowCache;
        }
        opacity.AddValueChanged(ShadowHost, OpacityChanged);
        if (ShadowHost.Opacity <= 0) ShadowHost.CacheMode = null;
        SourceInitialized += (_, _) => FitShadowCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
        Closed += (_, _) =>
        {
            // Property descriptors retain their targets until the handlers are removed, even after the window closes.
            corners.RemoveValueChanged(Island, ShapeChanged);
            shoulders.RemoveValueChanged(Island, ShapeChanged);
            opacity.RemoveValueChanged(ShadowHost, OpacityChanged);
            _shadowSettle.Stop();
            _shadowSettle = null;
        };
    }

    private void ShadowChanging()
    {
        if (_shadowSettle is null) return;
        ShadowHost.CacheMode = null; _shadowSettle.Stop(); _shadowSettle.Start();
    }

    private void UpdateShadowMask()
    {
        // The copy paints the same content the island does; a mask hides its inside so only the blur stays visible.
        double w = Island.ActualWidth, h = Island.ActualHeight;
        if (w <= 0 || h <= 0) { IslandShadow.OpacityMask = null; return; }
        // The mask's frame must reach past the effect's blur, or everything outside the island's rectangle would be cut:
        // the brush paints nothing beyond its viewbox, and the shadow's reach is the blur radius plus the shadow depth,
        // plus a small margin — the same reach the shadow bounds use below.
        var effect = IslandShadow.Effect as System.Windows.Media.Effects.DropShadowEffect;
        double reach = effect is null ? 0 : effect.BlurRadius + effect.ShadowDepth + 2;
        var area = new Rect(-reach, -reach, w + 2 * reach, h + 2 * reach);
        var mask = Geometry.Combine(new RectangleGeometry(area), Island.Outline(), GeometryCombineMode.Exclude, null);
        IslandShadow.OpacityMask = new DrawingBrush
        {
            Viewbox = area, ViewboxUnits = BrushMappingMode.Absolute, Viewport = area, ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            Drawing = new GeometryDrawing { Geometry = mask, Brush = Brushes.White }
        };
    }

    // The picture is drawn at the display's scale and reaches past the blur by whole device pixels (ShadowBounds): a picture
    // whose edge fell between pixels was resampled when drawn, and differed from the shadow drawn directly by up to 44 levels
    // along the island's edge at 125 %. Laid out on whole device pixels (UseLayoutRounding), it matches the uncached shadow
    // exactly at every scale (measured at 100 to 200 %). WPF already draws a cached picture at the system's DPI, so the cache
    // is asked only for what this monitor's scale adds to it. Asked again when the theme or the scale changes.
    private void FitShadowCache(double scale)
    {
        if (_shadowCache is not { } cache || IslandShadow.Effect is not DropShadowEffect effect || !(scale > 0)) return;
        double render = scale / SystemScale;
        if (cache.RenderAtScale != render) cache.RenderAtScale = render;
        double reach = Math.Ceiling((effect.BlurRadius + effect.ShadowDepth + 2) * scale) / scale;
        var margin = new Thickness(-reach);
        if (ShadowBounds.Margin != margin) ShadowBounds.Margin = margin;
    }

    private static readonly double SystemScale = GetDpiForSystem() is var dpi and > 0 ? dpi / 96d : 1;
    [DllImport("user32")] private static extern uint GetDpiForSystem();
}
