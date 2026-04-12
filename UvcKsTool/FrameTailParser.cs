using System.Buffers.Binary;
using System.Text;

namespace UvcKsTool;

internal static class FrameTailParser
{
    public const int MetadataRows = 4;
    private const int ParameterBlockLength = 128;

    public static FrameTailMetadata ParseMetadata(RawFramePacket packet)
    {
        EnsureSupported(packet);

        var parameterBlock = ExtractParameterBlock(packet.RawBytes, packet.Width, packet.Height);
        var embeddedParameters = new ThermometryParams(
            ReadSingle(parameterBlock, 0),
            ReadSingle(parameterBlock, 4),
            ReadSingle(parameterBlock, 8),
            ReadSingle(parameterBlock, 12),
            ReadSingle(parameterBlock, 16),
            ReadInt16(parameterBlock, 20));

        var productVersion = Encoding.ASCII
            .GetString(parameterBlock, 112, 16)
            .TrimEnd('\0', ' ');

        return new FrameTailMetadata(embeddedParameters, productVersion, parameterBlock);
    }

    public static ushort[] ExtractThermalCounts(RawFramePacket packet)
    {
        EnsureSupported(packet);

        var thermalHeight = packet.Height - MetadataRows;
        var pixelCount = packet.Width * thermalHeight;
        var expectedBytes = pixelCount * sizeof(ushort);
        var rawBytes = packet.RawBytes.AsSpan(0, expectedBytes);

        var counts = new ushort[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            counts[i] = BinaryPrimitives.ReadUInt16LittleEndian(rawBytes.Slice(i * sizeof(ushort), sizeof(ushort)));
        }

        return counts;
    }

    private static byte[] ExtractParameterBlock(byte[] rawBytes, int width, int height)
    {
        var bytesPerRow = checked(width * sizeof(ushort));
        var baseOffset = width switch
        {
            256 => (height * bytesPerRow) - 0x502,
            240 => (height * bytesPerRow) - 0x4A2,
            384 => (height * bytesPerRow) - 0x202,
            640 => (height * bytesPerRow) - 0x402,
            _ => throw new NotSupportedException($"Unsupported thermal width for parameter parsing: {width}.")
        };

        if (baseOffset < 0 || baseOffset + 112 > rawBytes.Length)
        {
            throw new InvalidOperationException("Computed parameter block is outside the frame payload.");
        }

        var parameterBlock = new byte[ParameterBlockLength];
        Buffer.BlockCopy(rawBytes, baseOffset, parameterBlock, 0, 112);

        var versionOffset = baseOffset - 0xCE;
        if (versionOffset < 0 || versionOffset + 16 > rawBytes.Length)
        {
            throw new InvalidOperationException("Computed product-version bytes are outside the frame payload.");
        }

        Buffer.BlockCopy(rawBytes, versionOffset, parameterBlock, 112, 16);
        return parameterBlock;
    }

    private static void EnsureSupported(RawFramePacket packet)
    {
        if (packet.TransportFormat != RawTransportFormat.UInt16LittleEndian)
        {
            throw new NotSupportedException($"Unsupported transport format: {packet.TransportFormat}.");
        }

        if (packet.Width <= 0 || packet.Height <= MetadataRows)
        {
            throw new InvalidOperationException($"Unsupported thermal dimensions: {packet.Width}x{packet.Height}.");
        }

        var expectedLength = checked(packet.Width * packet.Height * sizeof(ushort));
        if (packet.RawBytes.Length < expectedLength)
        {
            throw new InvalidOperationException(
                $"Raw payload too small for {packet.Width}x{packet.Height}x16-bit transport. " +
                $"Expected at least {expectedLength} bytes, got {packet.RawBytes.Length}.");
        }
    }

    private static float ReadSingle(byte[] buffer, int offset) =>
        BitConverter.ToSingle(buffer, offset);

    private static short ReadInt16(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, sizeof(short)));
}
