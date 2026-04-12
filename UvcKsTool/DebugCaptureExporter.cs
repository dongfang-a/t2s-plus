using System.Text.Json;

namespace UvcKsTool;

internal static class DebugCaptureExporter
{
    public static string Export(
        RawFramePacket packet,
        RadiometricFrame frame,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var rawPath = Path.Combine(outputDirectory, "raw-frame.bin");
        var temperaturesPath = Path.Combine(outputDirectory, "temperatures.bin");
        var metadataPath = Path.Combine(outputDirectory, "metadata.json");

        File.WriteAllBytes(rawPath, packet.RawBytes);
        File.WriteAllBytes(temperaturesPath, FloatArrayToBytes(frame.Temperatures));

        var payload = new
        {
            packet.Width,
            packet.Height,
            packet.TransportFormat,
            packet.Timestamp,
            frame.ThermalWidth,
            frame.ThermalHeight,
            frame.ActiveParameters,
            frame.State,
            frame.TailMetadata.ProductVersion,
            frame.TailMetadata.EmbeddedParameters,
            Header = frame.Header,
            frame.MaxPoint,
            frame.MinPoint,
            frame.Search.MaxRawCount,
            frame.Search.MinRawCount
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(metadataPath, json);
        return outputDirectory;
    }

    private static byte[] FloatArrayToBytes(float[] values)
    {
        var buffer = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, buffer, 0, buffer.Length);
        return buffer;
    }
}
