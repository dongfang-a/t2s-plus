using System.Buffers.Binary;
using System.Drawing;
using System.Text.Json;
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
    public void ParseNativeCalibration_ReadsKnownOffsetsFromTailBlock()
    {
        var frame = CreateModerateRawFrame();

        Assert.True(FrameTailParser.TryParseNativeCalibration(frame, new CameraThermometryState().Snapshot(), out var calibration));
        Assert.Equal(25f, calibration.FpaTemp, 1);
        Assert.Equal(20f, calibration.ShutterTemp, 0);
        Assert.Equal(1f, calibration.A, 3);
        Assert.Equal(-293.15f, calibration.B, 2);
        Assert.Equal(0.5f, calibration.Kc, 3);
        Assert.Equal(2213, calibration.RawBase);
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

        Assert.Equal(ThermometryAlgorithmMode.Native, state.Snapshot().AlgorithmMode);

        state.UpdateRangeMode(CameraThermometryState.WideRangeMode);
        state.UpdateCameraLens(CameraThermometryState.AlternateCameraLens);
        state.UpdateAlgorithmMode(ThermometryAlgorithmMode.Legacy);
        state.UpdateShutterFix(1.5f);

        Assert.True(state.Snapshot().Dirty);
        Assert.Equal(ThermometryAlgorithmMode.Legacy, state.Snapshot().AlgorithmMode);

        state.MarkDecoded();
        Assert.False(state.Snapshot().Dirty);
    }

    [Fact]
    public void Decoder_ReactsToParameterAndRangeChanges()
    {
        var frame = CreateModerateRawFrame();
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
        Assert.Equal(ThermometryDecodePath.NativeLookup, lowRange.DecodeInfo.Path);
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

    [Fact]
    public void DebugExport_MetadataIncludesAlgorithmMode()
    {
        var frame = CreateRenderedFrame() with
        {
            State = new CameraThermometryStateSnapshot(
                CameraThermometryState.NormalRangeMode,
                CameraThermometryState.DefaultCameraLens,
                ThermometryAlgorithmMode.Legacy,
                0f,
                false)
        };
        var packet = CreateRawFrame();
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"uvcks-debug-{Guid.NewGuid():N}");

        try
        {
            DebugCaptureExporter.Export(packet, frame, outputDirectory);
            var metadataPath = Path.Combine(outputDirectory, "metadata.json");
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var algorithmMode = document.RootElement.GetProperty("State").GetProperty("AlgorithmMode");

            Assert.True(
                algorithmMode.ValueKind == JsonValueKind.Number
                || algorithmMode.ValueKind == JsonValueKind.String);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ThermometryParameters_ChangeReportedTemperatures()
    {
        var frame = CreateModerateRawFrame();
        var baseline = DecodeFrame(frame, ThermometryParams.Default);

        var fixAdjusted = DecodeFrame(frame, baseline.ActiveParameters with { Fix = 10f });
        var reflectedAdjusted = DecodeFrame(frame, baseline.ActiveParameters with { ReflectedTemp = 80f });
        var ambientAdjusted = DecodeFrame(frame, baseline.ActiveParameters with { AmbientTemp = 40f });
        var humidityAdjusted = DecodeFrame(frame, baseline.ActiveParameters with { Humidity = 100f });
        var emissivityAdjusted = DecodeFrame(frame, baseline.ActiveParameters with { Emissivity = 0.80f });
        var distanceAdjusted = DecodeFrame(frame, baseline.ActiveParameters with { Distance = 200 });

        Assert.Equal(baseline.CenterTemp + 10f, fixAdjusted.CenterTemp, 3);
        Assert.True(reflectedAdjusted.CenterTemp < baseline.CenterTemp);
        Assert.NotEqual(baseline.CenterTemp, ambientAdjusted.CenterTemp);
        Assert.NotEqual(baseline.CenterTemp, humidityAdjusted.CenterTemp);
        Assert.True(emissivityAdjusted.CenterTemp > baseline.CenterTemp);
        Assert.NotEqual(baseline.CenterTemp, distanceAdjusted.CenterTemp);
    }

    [Fact]
    public void OverrangeTransportRaw_StillUsesNativeLookup()
    {
        var frame = CreateOverrangeRawFrame();

        var decoded = DecodeFrame(frame, ThermometryParams.Default);

        Assert.Equal(ThermometryDecodePath.NativeLookup, decoded.DecodeInfo.Path);
        Assert.Contains("direct transport raw", decoded.DecodeInfo.Reason);
        Assert.NotEqual(0f, decoded.CenterTemp);
        Assert.NotEqual(decoded.MinTemp, decoded.MaxTemp);
        Assert.Contains(decoded.Temperatures, value => value != 0f);
    }

    [Fact]
    public void NativeMode_ReportsExplicitFallbackReasonWhenUnavailable()
    {
        var decoded = DecodeFrame(CreateRawFrame(), ThermometryParams.Default, algorithmMode: ThermometryAlgorithmMode.Native);

        Assert.Equal(ThermometryDecodePath.LegacyFallback, decoded.DecodeInfo.Path);
        Assert.Contains("native selected unavailable", decoded.DecodeInfo.Reason);
    }

    [Fact]
    public void Decoder_UsesCalibratedRawAfterCapturingKTableAndShutterReference()
    {
        var decoder = new ManagedThermometryDecoder();
        var calibrationController = Assert.IsAssignableFrom<INativeCalibrationController>(decoder);
        var state = new CameraThermometryState();

        calibrationController.BeginKTableCapture();
        calibrationController.UpdateSourceMode(CaptureSourceMode.KTable);
        _ = decoder.Decode(CreateKTableFrame(), ThermometryParams.Default, state);

        Assert.True(calibrationController.TryDequeueCalibrationEvent(out var kTableEvent));
        Assert.Equal(NativeCalibrationKind.KTable, kTableEvent.Kind);

        calibrationController.UpdateSourceMode(CaptureSourceMode.Raw);
        calibrationController.BeginShutterCapture();
        for (var i = 0; i < 6; i++)
        {
            _ = decoder.Decode(CreateFlatRawFrame(1000), ThermometryParams.Default, state);
        }

        var shutterCaptured = false;
        while (calibrationController.TryDequeueCalibrationEvent(out var calibrationEvent))
        {
            if (calibrationEvent.Kind == NativeCalibrationKind.Shutter)
            {
                shutterCaptured = true;
            }
        }

        Assert.True(shutterCaptured);

        var decoded = decoder.Decode(CreateOverrangeRawFrame(), ThermometryParams.Default, state);

        Assert.Equal(ThermometryDecodePath.NativeLookup, decoded.DecodeInfo.Path);
        Assert.Contains("calibrated raw", decoded.DecodeInfo.Reason);
        Assert.True(decoded.Header[7] > 6000f);
    }

    [Fact]
    public void LegacyMode_ForcesLegacyPathAndIgnoresNativeCalibrationCaches()
    {
        var decoder = new ManagedThermometryDecoder();
        var calibrationController = Assert.IsAssignableFrom<INativeCalibrationController>(decoder);
        var nativeState = new CameraThermometryState();

        calibrationController.BeginKTableCapture();
        calibrationController.UpdateSourceMode(CaptureSourceMode.KTable);
        _ = decoder.Decode(CreateKTableFrame(), ThermometryParams.Default, nativeState);
        while (calibrationController.TryDequeueCalibrationEvent(out _))
        {
        }

        calibrationController.UpdateSourceMode(CaptureSourceMode.Raw);
        calibrationController.BeginShutterCapture();
        for (var i = 0; i < 6; i++)
        {
            _ = decoder.Decode(CreateFlatRawFrame(1000), ThermometryParams.Default, nativeState);
        }

        while (calibrationController.TryDequeueCalibrationEvent(out _))
        {
        }

        var nativeDecoded = decoder.Decode(CreateOverrangeRawFrame(), ThermometryParams.Default, nativeState);

        var legacyState = new CameraThermometryState();
        legacyState.UpdateAlgorithmMode(ThermometryAlgorithmMode.Legacy);
        var legacyDecoded = decoder.Decode(CreateOverrangeRawFrame(), ThermometryParams.Default, legacyState);

        Assert.Equal(ThermometryDecodePath.NativeLookup, nativeDecoded.DecodeInfo.Path);
        Assert.Contains("calibrated raw", nativeDecoded.DecodeInfo.Reason);
        Assert.Equal(ThermometryDecodePath.LegacyFallback, legacyDecoded.DecodeInfo.Path);
        Assert.Equal("legacy selected", legacyDecoded.DecodeInfo.Reason);
        Assert.NotEqual(nativeDecoded.CenterTemp, legacyDecoded.CenterTemp);
        Assert.True(legacyDecoded.Header[7] < nativeDecoded.Header[7]);
    }

    [Fact]
    public void StateParameters_ChangeReportedTemperatures()
    {
        var frame = CreateModerateRawFrame();
        var baseline = DecodeFrame(frame, ThermometryParams.Default);
        var shutterAdjusted = DecodeFrame(frame, ThermometryParams.Default, shutterFix: 5f);
        var wideRangeAdjusted = DecodeFrame(frame, ThermometryParams.Default, rangeMode: CameraThermometryState.WideRangeMode);
        var alternateLensAdjusted = DecodeFrame(frame, ThermometryParams.Default, cameraLens: CameraThermometryState.AlternateCameraLens);

        Assert.True(shutterAdjusted.CenterTemp < baseline.CenterTemp);
        Assert.NotEqual(baseline.CenterTemp, wideRangeAdjusted.CenterTemp);
        Assert.NotEqual(baseline.CenterTemp, alternateLensAdjusted.CenterTemp);
    }

    [Fact]
    public void AutoNormalizedPreview_CanStayIdenticalWhileTemperaturesChange()
    {
        var frame = CreateModerateRawFrame();
        var baseline = DecodeFrame(frame, ThermometryParams.Default);
        var adjusted = DecodeFrame(frame, baseline.ActiveParameters with { Fix = 10f });

        using var baselinePreview = TemperaturePreviewRenderer.Render(baseline, 0);
        using var adjustedPreview = TemperaturePreviewRenderer.Render(adjusted, 0);

        Assert.NotEqual(baseline.CenterTemp, adjusted.CenterTemp);
        Assert.True(BitmapsEqual(baselinePreview, adjustedPreview));
    }

    [Fact]
    public void PreviewFrameScheduler_LimitsProcessingToConfiguredCadence()
    {
        var scheduler = new PreviewFrameScheduler(TimeSpan.FromMilliseconds(40));

        Assert.True(scheduler.ShouldProcess(TimeSpan.Zero));
        Assert.False(scheduler.ShouldProcess(TimeSpan.FromMilliseconds(10)));
        Assert.False(scheduler.ShouldProcess(TimeSpan.FromMilliseconds(39)));
        Assert.True(scheduler.ShouldProcess(TimeSpan.FromMilliseconds(40)));
        Assert.False(scheduler.ShouldProcess(TimeSpan.FromMilliseconds(70)));
        Assert.True(scheduler.ShouldProcess(TimeSpan.FromMilliseconds(80)));
    }

    [Fact]
    public void LatestOnlyUiQueue_OnlyKeepsMostRecentPendingUpdate()
    {
        var queue = new LatestOnlyUiQueue<int>();

        Assert.True(queue.Enqueue(1));
        Assert.False(queue.Enqueue(2));
        Assert.True(queue.HasInvokeQueued);
        Assert.True(queue.HasPendingUpdate);

        Assert.True(queue.TryTakeLatest(out var first));
        Assert.Equal(2, first);
        Assert.True(queue.HasInvokeQueued);
        Assert.False(queue.HasPendingUpdate);

        Assert.False(queue.Enqueue(3));
        Assert.True(queue.HasPendingUpdate);

        Assert.True(queue.TryTakeLatest(out var second));
        Assert.Equal(3, second);
        Assert.False(queue.HasPendingUpdate);

        Assert.False(queue.TryTakeLatest(out _));
        Assert.False(queue.HasInvokeQueued);
    }

    [Fact]
    public void NativeLookupCache_ReusesLookupForIdenticalInputs()
    {
        var decoder = new ManagedThermometryDecoder();
        var state = new CameraThermometryState();
        var frame = CreateModerateRawFrame();

        _ = decoder.Decode(frame, ThermometryParams.Default, state);
        _ = decoder.Decode(frame, ThermometryParams.Default, state);

        Assert.Equal(1, decoder.NativeLookupBuildCount);
    }

    [Fact]
    public void NativeLookupCache_DoesNotRebuildWhenOnlyFixChanges()
    {
        var decoder = new ManagedThermometryDecoder();
        var state = new CameraThermometryState();
        var frame = CreateModerateRawFrame();

        var baseline = decoder.Decode(frame, ThermometryParams.Default, state);
        var adjusted = decoder.Decode(frame, ThermometryParams.Default with { Fix = 10f }, state);

        Assert.Equal(1, decoder.NativeLookupBuildCount);
        Assert.Equal(baseline.CenterTemp + 10f, adjusted.CenterTemp, 3);
    }

    [Fact]
    public void NativeLookupCache_RebuildsWhenAmbientChanges()
    {
        var decoder = new ManagedThermometryDecoder();
        var state = new CameraThermometryState();
        var frame = CreateModerateRawFrame();

        _ = decoder.Decode(frame, ThermometryParams.Default, state);
        _ = decoder.Decode(frame, ThermometryParams.Default with { AmbientTemp = 35f }, state);

        Assert.Equal(2, decoder.NativeLookupBuildCount);
    }

    [Fact]
    public void NativeLookupCache_RebuildsWhenCalibrationChanges()
    {
        var decoder = new ManagedThermometryDecoder();
        var state = new CameraThermometryState();
        var baselineFrame = CreateModerateRawFrame();
        var adjustedFrame = CreateModerateRawFrame();
        WriteNativeCalibration256(adjustedFrame.RawBytes, adjustedFrame.Width, adjustedFrame.Height, rawBase: 2300);

        _ = decoder.Decode(baselineFrame, ThermometryParams.Default, state);
        _ = decoder.Decode(adjustedFrame, ThermometryParams.Default, state);

        Assert.Equal(2, decoder.NativeLookupBuildCount);
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

    private static RawFramePacket CreateModerateRawFrame()
    {
        const int width = 256;
        const int height = 196;
        var raw = new byte[width * height * sizeof(ushort)];

        for (var y = 0; y < 192; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var rawCount = (ushort)(1800 + (x * 5) + (y * 4));
                var index = (y * width) + x;
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(index * sizeof(ushort), sizeof(ushort)), rawCount);
            }
        }

        WriteParameterBlock(raw, width, height, 0f, 20f, 20f, 50f, 0.95f, 1, "test-version");
        WriteNativeCalibration256(raw, width, height);
        return new RawFramePacket(width, height, DateTimeOffset.Parse("2026-04-12T12:00:00Z"), raw, RawTransportFormat.UInt16LittleEndian);
    }

    private static RawFramePacket CreateOverrangeRawFrame()
    {
        const int width = 256;
        const int height = 196;
        var raw = new byte[width * height * sizeof(ushort)];

        for (var y = 0; y < 192; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var rawCount = (ushort)(5200 + (x * 2) + y);
                var index = (y * width) + x;
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(index * sizeof(ushort), sizeof(ushort)), rawCount);
            }
        }

        WriteParameterBlock(raw, width, height, 0f, 25f, 25f, 0.4f, 0.98f, 1, "device");
        WriteNativeCalibration256(raw, width, height);
        return new RawFramePacket(width, height, DateTimeOffset.Parse("2026-04-12T12:00:00Z"), raw, RawTransportFormat.UInt16LittleEndian);
    }

    private static RawFramePacket CreateFlatRawFrame(ushort rawCount)
    {
        const int width = 256;
        const int height = 196;
        var raw = new byte[width * height * sizeof(ushort)];

        for (var y = 0; y < 192; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(index * sizeof(ushort), sizeof(ushort)), rawCount);
            }
        }

        WriteParameterBlock(raw, width, height, 0f, 20f, 20f, 50f, 0.95f, 1, "flat");
        WriteNativeCalibration256(raw, width, height);
        return new RawFramePacket(width, height, DateTimeOffset.Parse("2026-04-12T12:00:00Z"), raw, RawTransportFormat.UInt16LittleEndian);
    }

    private static RawFramePacket CreateKTableFrame()
    {
        const int width = 256;
        const int height = 196;
        var raw = new byte[width * height * sizeof(ushort)];

        for (var y = 0; y < 192; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var gain = (ushort)16383;
                if ((x == 80 && y == 40) || (x == 81 && y == 40))
                {
                    gain = 0;
                }

                var index = (y * width) + x;
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(index * sizeof(ushort), sizeof(ushort)), gain);
            }
        }

        WriteParameterBlock(raw, width, height, 0f, 20f, 20f, 50f, 0.95f, 1, "k-table");
        WriteNativeCalibration256(raw, width, height);
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

    private static void WriteNativeCalibration256(
        byte[] rawBytes,
        int width,
        int height,
        float fpaTemp = 25f,
        float shutterTemp = 20f,
        float calibrateCorrect = 0f,
        float a = 1f,
        float b = -293.15f,
        float ka = 0f,
        float kb = 0f,
        float kc = 0.5f,
        ushort rawBase = 2213)
    {
        const int calibrationBase = 0x100;
        var tailOffset = width * (height - FrameTailParser.MetadataRows) * sizeof(ushort);
        var fpaRaw = (ushort)Math.Round(8617f - ((fpaTemp - 20f) * 37.682f));
        var shutterRaw = (ushort)Math.Round((shutterTemp + 273.15f) * 10f);

        BinaryPrimitives.WriteUInt16LittleEndian(rawBytes.AsSpan(tailOffset + (1 * sizeof(ushort)), sizeof(ushort)), fpaRaw);
        BinaryPrimitives.WriteUInt16LittleEndian(rawBytes.AsSpan(tailOffset + (calibrationBase * sizeof(ushort)), sizeof(ushort)), rawBase);
        BinaryPrimitives.WriteUInt16LittleEndian(rawBytes.AsSpan(tailOffset + ((calibrationBase + 1) * sizeof(ushort)), sizeof(ushort)), shutterRaw);
        WriteSingle(rawBytes, tailOffset + ((calibrationBase + 3) * sizeof(ushort)), a);
        WriteSingle(rawBytes, tailOffset + ((calibrationBase + 5) * sizeof(ushort)), b);
        WriteSingle(rawBytes, tailOffset + ((calibrationBase + 7) * sizeof(ushort)), ka);
        WriteSingle(rawBytes, tailOffset + ((calibrationBase + 9) * sizeof(ushort)), kb);
        WriteSingle(rawBytes, tailOffset + ((calibrationBase + 11) * sizeof(ushort)), kc);
        WriteSingle(rawBytes, tailOffset + ((calibrationBase + 13) * sizeof(ushort)), calibrateCorrect);
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
            new CameraThermometryStateSnapshot(CameraThermometryState.NormalRangeMode, CameraThermometryState.DefaultCameraLens, ThermometryAlgorithmMode.Native, 0f, false),
            new FrameTailMetadata(ThermometryParams.Default, "test", new byte[128]),
            ThermometryDecodeInfo.NativeLookup);
    }

    private static RadiometricFrame DecodeFrame(
        RawFramePacket frame,
        ThermometryParams parameters,
        ThermometryAlgorithmMode algorithmMode = ThermometryAlgorithmMode.Native,
        int rangeMode = CameraThermometryState.NormalRangeMode,
        int cameraLens = CameraThermometryState.DefaultCameraLens,
        float shutterFix = 0f)
    {
        var decoder = new ManagedThermometryDecoder();
        var state = new CameraThermometryState();

        if (algorithmMode != ThermometryAlgorithmMode.Native)
        {
            state.UpdateAlgorithmMode(algorithmMode);
        }

        if (rangeMode != CameraThermometryState.NormalRangeMode)
        {
            state.UpdateRangeMode(rangeMode);
        }

        if (cameraLens != CameraThermometryState.DefaultCameraLens)
        {
            state.UpdateCameraLens(cameraLens);
        }

        if (Math.Abs(shutterFix) > float.Epsilon)
        {
            state.UpdateShutterFix(shutterFix);
        }

        return decoder.Decode(frame, parameters, state);
    }

    private static bool BitmapsEqual(Bitmap left, Bitmap right)
    {
        if (left.Width != right.Width || left.Height != right.Height)
        {
            return false;
        }

        for (var y = 0; y < left.Height; y++)
        {
            for (var x = 0; x < left.Width; x++)
            {
                if (left.GetPixel(x, y).ToArgb() != right.GetPixel(x, y).ToArgb())
                {
                    return false;
                }
            }
        }

        return true;
    }
}
