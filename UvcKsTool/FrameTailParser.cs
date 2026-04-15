using System.Buffers.Binary;
using System.Text;

namespace UvcKsTool;

internal readonly record struct NativeTailCalibration(
    int Width,
    float FpaTemp,
    float ShutterTemp,
    float CalibrateCorrect,
    float A,
    float B,
    float Ka,
    float Kb,
    float Kc,
    ushort RawBase);

internal static class FrameTailParser
{
    public const int MetadataRows = 4;
    private const int ParameterBlockLength = 128;
    private const int Width256CalibrationBase = 0x100;

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

    public static bool TryParseNativeCalibration(
        RawFramePacket packet,
        CameraThermometryStateSnapshot state,
        out NativeTailCalibration calibration)
    {
        EnsureSupported(packet);
        calibration = default;

        if (packet.Width != 256)
        {
            return false;
        }

        var tailBytes = ExtractTailBytes(packet);
        var fpaRaw = ReadUInt16FromTail(tailBytes, 1);
        var fpaTemp = 20f + ((fpaRaw - 8617f) / -37.682f);
        var shutterTemp = (ReadUInt16FromTail(tailBytes, Width256CalibrationBase + 1) / 10f) - 273.15f;
        var a = ReadSingleFromTail(tailBytes, Width256CalibrationBase + 3);
        var b = ReadSingleFromTail(tailBytes, Width256CalibrationBase + 5);
        var ka = ReadSingleFromTail(tailBytes, Width256CalibrationBase + 7);
        var kb = ReadSingleFromTail(tailBytes, Width256CalibrationBase + 9);
        var kc = ReadSingleFromTail(tailBytes, Width256CalibrationBase + 11);
        var calibrateCorrect = ReadSingleFromTail(tailBytes, Width256CalibrationBase + 13);
        var rawBase = ReadUInt16FromTail(tailBytes, Width256CalibrationBase);

        if (!float.IsFinite(fpaTemp) ||
            !float.IsFinite(shutterTemp) ||
            !float.IsFinite(a) ||
            !float.IsFinite(b) ||
            !float.IsFinite(ka) ||
            !float.IsFinite(kb) ||
            !float.IsFinite(kc) ||
            !float.IsFinite(calibrateCorrect) ||
            Math.Abs(a) < float.Epsilon)
        {
            return false;
        }

        calibration = new NativeTailCalibration(
            packet.Width,
            fpaTemp,
            shutterTemp,
            calibrateCorrect,
            a,
            b,
            ka,
            kb,
            kc,
            rawBase);

        return true;
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

    private static ReadOnlySpan<byte> ExtractTailBytes(RawFramePacket packet)
    {
        var thermalHeight = packet.Height - MetadataRows;
        var tailOffset = checked(packet.Width * thermalHeight * sizeof(ushort));
        return packet.RawBytes.AsSpan(tailOffset);
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

    private static ushort ReadUInt16FromTail(ReadOnlySpan<byte> tailBytes, int wordOffset)
    {
        var byteOffset = checked(wordOffset * sizeof(ushort));
        return BinaryPrimitives.ReadUInt16LittleEndian(tailBytes.Slice(byteOffset, sizeof(ushort)));
    }

    private static float ReadSingleFromTail(ReadOnlySpan<byte> tailBytes, int wordOffset)
    {
        var byteOffset = checked(wordOffset * sizeof(ushort));
        return BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(tailBytes.Slice(byteOffset, sizeof(float))));
    }
}
