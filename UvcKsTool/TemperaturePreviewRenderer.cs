using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace UvcKsTool;

internal static class TemperaturePreviewRenderer
{
    public static Bitmap Render(RadiometricFrame frame)
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
                    var color = MapColor(normalized);
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
        DrawMarker(graphics, frame.MaxPoint, Color.Red);
        DrawMarker(graphics, frame.MinPoint, Color.DeepSkyBlue);
        return bitmap;
    }

    private static Color MapColor(float normalized)
    {
        if (normalized < 0.25f)
        {
            return Blend(Color.FromArgb(15, 15, 30), Color.DarkBlue, normalized / 0.25f);
        }

        if (normalized < 0.5f)
        {
            return Blend(Color.DarkBlue, Color.Cyan, (normalized - 0.25f) / 0.25f);
        }

        if (normalized < 0.75f)
        {
            return Blend(Color.Cyan, Color.Yellow, (normalized - 0.5f) / 0.25f);
        }

        return Blend(Color.Yellow, Color.Red, (normalized - 0.75f) / 0.25f);
    }

    private static Color Blend(Color start, Color end, float t)
    {
        var clamped = Math.Clamp(t, 0f, 1f);
        var r = start.R + ((end.R - start.R) * clamped);
        var g = start.G + ((end.G - start.G) * clamped);
        var b = start.B + ((end.B - start.B) * clamped);
        return Color.FromArgb((int)r, (int)g, (int)b);
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
