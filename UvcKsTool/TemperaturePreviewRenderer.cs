using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace UvcKsTool;

internal static class TemperaturePreviewRenderer
{
    public static Bitmap Render(RadiometricFrame frame, int paletteIndex)
    {
        var bitmap = new Bitmap(frame.ThermalWidth, frame.ThermalHeight, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            bitmap.PixelFormat);

        try
        {
            var buffer = new byte[data.Stride * bitmap.Height];
            var min = frame.MinTemp;
            var max = frame.MaxTemp;
            var span = Math.Max(max - min, 0.001f);

            for (var y = 0; y < bitmap.Height; y++)
            {
                var rowOffset = y * data.Stride;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var index = (y * bitmap.Width) + x;
                    var normalized = Math.Clamp((frame.Temperatures[index] - min) / span, 0f, 1f);
                    var color = MapColor(normalized, paletteIndex);
                    var pixelOffset = rowOffset + (x * 3);
                    buffer[pixelOffset] = color.B;
                    buffer[pixelOffset + 1] = color.G;
                    buffer[pixelOffset + 2] = color.R;
                }
            }

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var graphics = Graphics.FromImage(bitmap);
        DrawCrosshair(graphics, GetCenterPoint(frame), Color.Gold);
        DrawMarker(graphics, frame.MaxPoint, Color.Red);
        DrawMarker(graphics, frame.MinPoint, Color.DeepSkyBlue);
        return bitmap;
    }

    private static Color MapColor(float normalized, int paletteIndex)
    {
        Color[] stops = paletteIndex switch
        {
            0 => [Color.Black, Color.DimGray, Color.Gainsboro, Color.White],
            1 => [Color.White, Color.Gainsboro, Color.DimGray, Color.Black],
            2 => [Color.FromArgb(12, 24, 88), Color.RoyalBlue, Color.OrangeRed, Color.Yellow],
            3 => [Color.FromArgb(56, 18, 84), Color.MediumPurple, Color.OrangeRed, Color.Yellow],
            4 => [Color.Navy, Color.SeaGreen, Color.Orange, Color.Red],
            5 => [Color.DarkBlue, Color.Cyan, Color.LimeGreen, Color.Yellow, Color.Red],
            6 => [Color.Purple, Color.DeepSkyBlue, Color.Lime, Color.Yellow, Color.OrangeRed],
            7 => [Color.Black, Color.Maroon, Color.Red, Color.Orange],
            8 => [Color.DarkOliveGreen, Color.DarkGreen, Color.OrangeRed, Color.Red],
            9 => [Color.Navy, Color.Teal, Color.OrangeRed, Color.HotPink],
            10 => [Color.Black, Color.DarkBlue, Color.Cyan, Color.Yellow, Color.Red, Color.White],
            11 => [Color.Black, Color.Firebrick, Color.Red, Color.Orange, Color.Yellow],
            _ => [Color.DarkBlue, Color.Cyan, Color.Yellow, Color.Red]
        };

        return InterpolateStops(stops, normalized);
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
