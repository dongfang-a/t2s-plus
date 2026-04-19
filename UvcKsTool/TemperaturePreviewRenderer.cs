using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace UvcKsTool;

internal sealed class ReusablePreviewSurface : IDisposable
{
    private readonly Bitmap _bitmap;
    private readonly byte[] _buffer;

    public ReusablePreviewSurface(int width, int height)
    {
        _bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = _bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            _bitmap.PixelFormat);

        try
        {
            Stride = data.Stride;
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }

        _buffer = new byte[Stride * height];
    }

    public Bitmap Bitmap => _bitmap;

    public int Width => _bitmap.Width;

    public int Height => _bitmap.Height;

    public int Stride { get; }

    public byte[] Buffer => _buffer;

    public void Dispose()
    {
        _bitmap.Dispose();
    }
}

internal static class TemperaturePreviewRenderer
{
    private static readonly IReadOnlyList<Color>[] Palettes =
    [
        [Color.Black, Color.DimGray, Color.Gainsboro, Color.White],
        [Color.White, Color.Gainsboro, Color.DimGray, Color.Black],
        [Color.FromArgb(12, 24, 88), Color.RoyalBlue, Color.OrangeRed, Color.Yellow],
        [Color.FromArgb(56, 18, 84), Color.MediumPurple, Color.OrangeRed, Color.Yellow],
        [Color.Navy, Color.SeaGreen, Color.Orange, Color.Red],
        [Color.DarkBlue, Color.Cyan, Color.LimeGreen, Color.Yellow, Color.Red],
        [Color.Purple, Color.DeepSkyBlue, Color.Lime, Color.Yellow, Color.OrangeRed],
        [Color.Black, Color.Maroon, Color.Red, Color.Orange],
        [Color.DarkOliveGreen, Color.DarkGreen, Color.OrangeRed, Color.Red],
        [Color.Navy, Color.Teal, Color.OrangeRed, Color.HotPink],
        [Color.Black, Color.DarkBlue, Color.Cyan, Color.Yellow, Color.Red, Color.White],
        [Color.Black, Color.Firebrick, Color.Red, Color.Orange, Color.Yellow]
    ];

    private static readonly IReadOnlyList<Color> DefaultPalette =
        [Color.DarkBlue, Color.Cyan, Color.Yellow, Color.Red];

    public static Bitmap Render(RadiometricFrame frame, int paletteIndex)
    {
        using var surface = new ReusablePreviewSurface(frame.ThermalWidth, frame.ThermalHeight);
        RenderInto(surface, frame, paletteIndex);
        return (Bitmap)surface.Bitmap.Clone();
    }

    public static void RenderInto(ReusablePreviewSurface surface, RadiometricFrame frame, int paletteIndex)
    {
        if (surface.Width != frame.ThermalWidth || surface.Height != frame.ThermalHeight)
        {
            throw new ArgumentException("Preview surface size does not match frame dimensions.", nameof(surface));
        }

        var palette = GetPaletteStops(paletteIndex);
        FillPixelBuffer(surface.Buffer, surface.Stride, frame, palette);

        var data = surface.Bitmap.LockBits(
            new Rectangle(0, 0, surface.Width, surface.Height),
            ImageLockMode.WriteOnly,
            surface.Bitmap.PixelFormat);

        try
        {
            Marshal.Copy(surface.Buffer, 0, data.Scan0, surface.Buffer.Length);
        }
        finally
        {
            surface.Bitmap.UnlockBits(data);
        }

        using var graphics = Graphics.FromImage(surface.Bitmap);
        DrawCrosshair(graphics, GetCenterPoint(frame), Color.Gold);
        DrawMarker(graphics, frame.MaxPoint, Color.Red);
        DrawMarker(graphics, frame.MinPoint, Color.DeepSkyBlue);
    }

    private static IReadOnlyList<Color> GetPaletteStops(int paletteIndex)
    {
        if ((uint)paletteIndex < (uint)Palettes.Length)
        {
            return Palettes[paletteIndex];
        }

        return DefaultPalette;
    }

    private static void FillPixelBuffer(
        byte[] buffer,
        int stride,
        RadiometricFrame frame,
        IReadOnlyList<Color> palette)
    {
        var min = frame.MinTemp;
        var max = frame.MaxTemp;
        var span = Math.Max(max - min, 0.001f);

        for (var y = 0; y < frame.ThermalHeight; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < frame.ThermalWidth; x++)
            {
                var index = (y * frame.ThermalWidth) + x;
                var normalized = Math.Clamp((frame.Temperatures[index] - min) / span, 0f, 1f);
                var color = InterpolateStops(palette, normalized);
                var pixelOffset = rowOffset + (x * 3);
                buffer[pixelOffset] = color.B;
                buffer[pixelOffset + 1] = color.G;
                buffer[pixelOffset + 2] = color.R;
            }
        }
    }

    private static Color Blend(Color start, Color end, float t)
    {
        var clamped = Math.Clamp(t, 0f, 1f);
        var r = start.R + ((end.R - start.R) * clamped);
        var g = start.G + ((end.G - start.G) * clamped);
        var b = start.B + ((end.B - start.B) * clamped);
        return Color.FromArgb((int)r, (int)g, (int)b);
    }

    private static Color InterpolateStops(IReadOnlyList<Color> stops, float normalized)
    {
        if (stops.Count == 0)
        {
            return Color.Black;
        }

        if (stops.Count == 1)
        {
            return stops[0];
        }

        var clamped = Math.Clamp(normalized, 0f, 1f);
        var scaled = clamped * (stops.Count - 1);
        var lowerIndex = Math.Min((int)Math.Floor(scaled), stops.Count - 2);
        var upperIndex = lowerIndex + 1;
        var localT = scaled - lowerIndex;
        return Blend(stops[lowerIndex], stops[upperIndex], localT);
    }

    private static Point GetCenterPoint(RadiometricFrame frame)
    {
        return new Point(frame.ThermalWidth / 2, frame.ThermalHeight / 2);
    }

    private static void DrawCrosshair(Graphics graphics, Point point, Color color)
    {
        const int innerGap = 3;
        const int outerLength = 10;
        using var pen = new Pen(color, 1.4f);
        graphics.DrawLine(pen, point.X - outerLength, point.Y, point.X - innerGap, point.Y);
        graphics.DrawLine(pen, point.X + innerGap, point.Y, point.X + outerLength, point.Y);
        graphics.DrawLine(pen, point.X, point.Y - outerLength, point.X, point.Y - innerGap);
        graphics.DrawLine(pen, point.X, point.Y + innerGap, point.X, point.Y + outerLength);
    }

    private static void DrawMarker(Graphics graphics, Point point, Color color)
    {
        const int size = 6;
        using var pen = new Pen(color, 1.2f);
        graphics.DrawEllipse(pen, point.X - size / 2, point.Y - size / 2, size, size);
        graphics.DrawLine(pen, point.X - size, point.Y, point.X + size, point.Y);
        graphics.DrawLine(pen, point.X, point.Y - size, point.X, point.Y + size);
    }
}
