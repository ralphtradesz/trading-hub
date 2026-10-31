using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using TradingHub.Rendering;

namespace TradingHub.App.Controls;

/// <summary>
/// Control que dibuja con SkiaSharp. Graba los comandos en un SKPicture en el hilo de UI
/// (donde viven los datos) y el hilo de render solo lo reproduce: sin locks y con aceleración.
/// </summary>
public abstract class SkiaView : Control
{
    protected SkiaView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    protected abstract void Draw(SKCanvas canvas, float width, float height);

    /// <summary>Esquinas redondeadas del dibujo (0 = rectas), para que el fondo acompañe al recuadro que lo contiene.</summary>
    public double CornerRadius { get; set; }

    public override void Render(DrawingContext context)
    {
        float w = (float)Bounds.Width, h = (float)Bounds.Height;
        if (w < 2 || h < 2) return;
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, w, h));
        if (CornerRadius > 0)
        {
            float r = (float)CornerRadius;
            canvas.ClipRoundRect(new SKRoundRect(new SKRect(0, 0, w, h), r, r), SKClipOperation.Intersect, antialias: true);
        }

        try
        {
            Draw(canvas, w, h);
        }
        catch (Exception ex)
        {
            using var p = new SKPaint { Color = SKColors.OrangeRed, IsAntialias = true };
            canvas.DrawText(UiText.Tr("Error al dibujar: ") + ex.Message, 10, 20, p);
            CrashLog.Write(ex);
        }

        var picture = recorder.EndRecording();
        context.Custom(new PictureDrawOperation(new Rect(0, 0, w, h), picture));
    }

    private sealed class PictureDrawOperation : ICustomDrawOperation
    {
        private readonly SKPicture _picture;

        public PictureDrawOperation(Rect bounds, SKPicture picture)
        {
            Bounds = bounds;
            _picture = picture;
        }

        public Rect Bounds { get; }

        public void Dispose() => _picture.Dispose();

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null) return;
            using var lease = feature.Lease();
            lease.SkCanvas.DrawPicture(_picture);
        }
    }
}
