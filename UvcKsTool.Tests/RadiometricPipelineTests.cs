using System.Buffers.Binary;
using Xunit;

namespace UvcKsTool.Tests;

public sealed class RadiometricPipelineTests
{
    [Fact]
    public void ParseMetadata_ReadsKnownOffsetsFromTailBlock()
    {
        var frame = CreateRawFrame();
        WriteParameterBlock(frame.RawBytes, frame.Width, frame.Height,
            fix: 1.25f,
            reflected: 22.5f,
            ambient: 24.5f,
            humidity: 58.5f,
            emissivity: 0.97f,
            distance: 7,
            productVersion: "ver-1234567890");

        var metadata = FrameTailParser.ParseMetadata(frame);

        Assert.Equal(1.25f, metadata.EmbeddedParameters.Fix, 3);
        Assert.Equal(22.5f, metadata.EmbeddedParameters.ReflectedTemp, 3);
        Assert.Equal(24.5f, metadata.EmbeddedParameters.AmbientTemp, 3);
        Assert.Equal(58.5f, metadata.EmbeddedParameters.Humidity, 3);
        Assert.Equal(0.97f, metadata.EmbeddedParameters.Emissivity, 3);
        Assert.Equal(7, metadata.EmbeddedParameters.Distance);
        Assert.StartsWith("ver-1234567890", metadata.ProductVersion);
    }

    [Fact]
    public void ExtractThermalCounts_DropsMetadataRows()
    {
        var frame = CreateRawFrame();

        var counts = FrameTailParser.ExtractThermalCounts(frame);

        Assert.Equal(256 * 192, counts.Length);
        Assert.Equal(1000, counts[0]);
        Assert.Equal(1000 + (256 * 192) - 1, counts[^1]);
    }

    [Fact]
    public void SearchCore_UsesExpectedHeaderLayout()
    {
        var width = 4;
        var height = 3;
        var temperatures = new[]
        {
            10f, 12f, 14f, 16f,
            18f, 20f, 22f, 24f,
            26f, 28f, 30f, 32f
        };
        var rawCounts = Enumerable.Range(100, temperatures.Length).Select(value => (ushort)value).ToArray();

        var result = SearchCore.Search(temperatures, rawCounts, width, height);

        Assert.Equal(22f, result.Header[0]);
        Assert.Equal(3f, result.Header[1]);
        Assert.Equal(2f, result.Header[2]);
        Assert.Equal(32f, result.Header[3]);
        Assert.Equal(0f, result.Header[4]);
        Assert.Equal(0f, result.Header[5]);
        Assert.Equal(10f, result.Header[6]);
        Assert.Equal(rawCounts[^1], result.MaxRawCount);
        Assert.Equal(rawCounts[0], result.MinRawCount);
    }

    [Fact]
    public void ThermometryState_StaysDirtyUntilDecoded()
    {
        var state = new CameraThermometryState();
        state.MarkDecoded();

        state.UpdateRangeMode(CameraThermometryState.WideRangeMode);
        state.UpdateCameraLens(CameraThermometryState.AlternateCameraLens);
        state.UpdateShutterFix(1.5f);

        Assert.True(state.Snapshot().Dirty);

        state.MarkDecoded();
        Assert.False(state.Snapshot().Dirty);
    }

    [Fact]
    public void Decoder_ReactsToParameterAndRangeChanges()
    {
        var frame = CreateRawFrame();
        WriteParameterBlock(frame.RawBytes, frame.Width, frame.Height, 0f, 20f, 20f, 50f, 0.95f, 1, "test-version");

        var decoder = new ManagedThermometryDecoder();
        var state = new CameraThermometryState();
        var lowRange = decoder.Decode(frame, new ThermometryParams(0f, 20f, 20f, 50f, 0.95f, 1), state);

        state.UpdateRangeMode(CameraThermometryState.WideRangeMode);
        var highRange = decoder.Decode(frame, new ThermometryParams(2f, 25f, 30f, 65f, 0.88f, 5), state);

        Assert.Equal(256 * 192, lowRange.Temperatures.Length);
        Assert.Equal(TemperatureSearchResult.HeaderLength, lowRange.Header.Length);
        Assert.NotEqual(lowRange.CenterTemp, highRange.CenterTemp);
        Assert.Equal(CameraThermometryState.WideRangeMode, highRange.State.RangeMode);
        Assert.False(highRange.State.Dirty);
    }

    [Fact]
    public void Renderer_ChangesPixelColorsAcrossPalettes()
    {
        var frame = CreateRenderedFrame();

        using var whiteHot = TemperaturePreviewRenderer.Render(frame, 0);
        using var rainbow = TemperaturePreviewRenderer.Render(frame, 5);

        Assert.NotEqual(whiteHot.GetPixel(8, 8).ToArgb(), rainbow.GetPixel(8, 8).ToArgb());
    }

    private static RawFramePacket CreateRawFrame()
    {
        const int width = 256;
        const int height = 196;
        var raw = new byte[width * height * sizeof(ushort)];

        for (var i = 0; i < width * 192; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i * sizeof(ushort), sizeof(ushort)), (ushort)(1000 + i));
        }

        return new RawFramePacket(width, height, DateTimeOffset.Parse("2026-04-12T12:00:00Z"), raw, RawTransportFormat.UInt16LittleEndian);
    }

    private static void WriteParameterBlock(
        byte[] rawBytes,
        int width,
        int height,
        float fix,
        float reflected,
        float ambient,
        float humidity,
        float emissivity,
        short distance,
        string productVersion)
    {
        var bytesPerRow = width * sizeof(ushort);
        var baseOffset = (height * bytesPerRow) - 0x502;

        WriteSingle(rawBytes, baseOffset + 0, fix);
        WriteSingle(rawBytes, baseOffset + 4, reflected);
        WriteSingle(rawBytes, baseOffset + 8, ambient);
        WriteSingle(rawBytes, baseOffset + 12, humidity);
        WriteSingle(rawBytes, baseOffset + 16, emissivity);
        BinaryPrimitives.WriteInt16LittleEndian(rawBytes.AsSpan(baseOffset + 20, sizeof(short)), distance);

        var versionBytes = System.Text.Encoding.ASCII.GetBytes(productVersion.PadRight(16, '\0')[..16]);
        Buffer.BlockCopy(versionBytes, 0, rawBytes, baseOffset - 0xCE, versionBytes.Length);
    }

    private static void WriteSingle(byte[] buffer, int offset, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }

    private static RadiometricFrame CreateRenderedFrame()
    {
        const int width = 16;
        const int height = 16;
        var temperatures = new float[width * height];
        var rawCounts = new ushort[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                temperatures[index] = 10f + x + (y * 1.5f);
                rawCounts[index] = (ushort)(200 + index);
            }
        }

        var search = SearchCore.Search(temperatures, rawCounts, width, height);

        return new RadiometricFrame(
            DateTimeOffset.Parse("2026-04-12T12:00:00Z"),
            width,
            height,
            rawCounts,
            temperatures,
            search,
            ThermometryParams.Default,
            new CameraThermometryStateSnapshot(CameraThermometryState.NormalRangeMode, CameraThermometryState.DefaultCameraLens, 0f, false),
            new FrameTailMetadata(ThermometryParams.Default, "test", new byte[128]));
    }
}
