using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace KnA.App.Views;

/// <summary>
/// Visual trim: thumbnail strip + waveform, draggable in/out handles and a playhead.
/// Click/drag on the strip moves the playhead; drag near a handle moves the cut.
/// </summary>
public sealed class TrimTimeline : Grid
{
    private readonly UniformGrid _thumbs = new() { Rows = 1 };
    private readonly Image _wave = new() { Stretch = Stretch.Fill, Height = 70 };
    private readonly Overlay _overlay;

    public double Duration { get; }
    public double? Start { get; private set; }
    public double? End { get; private set; }
    public double Playhead { get; private set; }

    /// <summary>(start, end) after a handle drag; null = no cut on that side.</summary>
    public event Action<double?, double?>? TrimChanged;
    public event Action<double>? PlayheadChanged;

    public TrimTimeline(double duration, double? start, double? end, double playhead)
    {
        Duration = Math.Max(duration, 0.001);
        Start = start; End = end; Playhead = playhead;
        RowDefinitions = new RowDefinitions("Auto,Auto");
        ClipToBounds = true;
        var strip = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Height = 64, Child = _thumbs, Background = Brushes.Black };
        var wave = new Border { Background = new SolidColorBrush(Color.Parse("#22000000")), Child = _wave, Margin = new Thickness(0, 4, 0, 0) };
        SetRow(wave, 1);
        Children.Add(strip);
        Children.Add(wave);
        _overlay = new Overlay(this);
        SetRowSpan(_overlay, 2);
        Children.Add(_overlay);
    }

    public int ThumbCount => 12;

    public void SetThumb(int i, Bitmap bmp)
    {
        while (_thumbs.Children.Count < ThumbCount)
            _thumbs.Children.Add(new Image { Stretch = Stretch.UniformToFill });
        ((Image)_thumbs.Children[i]).Source = bmp;
    }

    public void SetWave(Bitmap? bmp) { _wave.Source = bmp; _wave.IsVisible = bmp is not null; }

    public void SetTrim(double? start, double? end) { Start = start; End = end; _overlay.InvalidateVisual(); }

    public void SetPlayhead(double t) { Playhead = Math.Clamp(t, 0, Duration); _overlay.InvalidateVisual(); }

    private sealed class Overlay : Control
    {
        private static readonly IBrush Shade = new SolidColorBrush(Color.Parse("#99000000"));
        private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#F43F5E"));
        private static readonly IBrush Head = Brushes.White;
        private readonly TrimTimeline _t;
        private string? _drag;

        public Overlay(TrimTimeline t) { _t = t; Cursor = new Cursor(StandardCursorType.Hand); }

        private double X(double sec) => Math.Clamp(sec / _t.Duration, 0, 1) * Bounds.Width;
        private double Sec(double x) => Math.Clamp(x / Math.Max(Bounds.Width, 1), 0, 1) * _t.Duration;

        public override void Render(DrawingContext ctx)
        {
            var h = Bounds.Height;
            double s = X(_t.Start ?? 0), e = X(_t.End ?? _t.Duration);
            ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            ctx.FillRectangle(Shade, new Rect(0, 0, s, h));
            ctx.FillRectangle(Shade, new Rect(e, 0, Bounds.Width - e, h));
            ctx.DrawRectangle(new Pen(Accent, 2), new Rect(s, 1, Math.Max(e - s, 1), h - 2));
            ctx.FillRectangle(Accent, new Rect(s - 4, 0, 8, h), 3);
            ctx.FillRectangle(Accent, new Rect(e - 4, 0, 8, h), 3);
            var p = X(_t.Playhead);
            ctx.FillRectangle(Head, new Rect(p - 1, 0, 2, h));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            var x = e.GetPosition(this).X;
            double s = X(_t.Start ?? 0), en = X(_t.End ?? _t.Duration);
            _drag = Math.Abs(x - s) < 9 ? "start" : Math.Abs(x - en) < 9 ? "end" : "play";
            e.Pointer.Capture(this);
            Move(x);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (_drag is not null) Move(e.GetPosition(this).X);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (_drag is "start" or "end") _t.TrimChanged?.Invoke(_t.Start, _t.End);
            _drag = null;
            e.Pointer.Capture(null);
        }

        private void Move(double x)
        {
            var sec = Sec(x);
            if (_drag == "start")
            {
                sec = Math.Min(sec, (_t.End ?? _t.Duration) - 0.05);
                _t.Start = sec > 0.0005 ? sec : null;
            }
            else if (_drag == "end")
            {
                sec = Math.Max(sec, (_t.Start ?? 0) + 0.05);
                _t.End = sec < _t.Duration - 0.0005 ? sec : null;
            }
            _t.Playhead = sec;
            _t.PlayheadChanged?.Invoke(sec);
            InvalidateVisual();
        }
    }
}
