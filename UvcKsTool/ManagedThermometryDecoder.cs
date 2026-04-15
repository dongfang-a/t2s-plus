namespace UvcKsTool;

internal sealed class ManagedThermometryDecoder : IThermometryDecoder, INativeCalibrationController
{
    private readonly NativeCalibrationState _calibrationState = new();

    public RadiometricFrame Decode(RawFramePacket packet, ThermometryParams parameters, CameraThermometryState state)
    {
        var snapshot = state.Snapshot();
        var tailMetadata = FrameTailParser.ParseMetadata(packet);
        var transportRawCounts = FrameTailParser.ExtractThermalCounts(packet);
        var thermalHeight = packet.Height - FrameTailParser.MetadataRows;

        _calibrationState.ObserveFrame(transportRawCounts, packet.Width, thermalHeight);

        var decodeResult = ThermometryCore.Decode(
            packet,
            transportRawCounts,
            packet.Width,
            thermalHeight,
            parameters,
            snapshot,
            _calibrationState);
        var search = SearchCore.Search(decodeResult.Temperatures, decodeResult.SearchRawCounts, packet.Width, thermalHeight);

        state.MarkDecoded();

        return new RadiometricFrame(
            packet.Timestamp,
            packet.Width,
            thermalHeight,
            decodeResult.SearchRawCounts,
            decodeResult.Temperatures,
            search,
            parameters,
            state.Snapshot(),
            tailMetadata,
            decodeResult.DecodeInfo);
    }

    public void UpdateSourceMode(CaptureSourceMode mode) => _calibrationState.UpdateSourceMode(mode);

    public void BeginKTableCapture() => _calibrationState.BeginKTableCapture();

    public void BeginShutterCapture() => _calibrationState.BeginShutterCapture();

    public bool TryDequeueCalibrationEvent(out NativeCalibrationEvent calibrationEvent) =>
        _calibrationState.TryDequeueCalibrationEvent(out calibrationEvent);
}

internal static class ThermometryCore
{
    public static TemperatureDecodeResult Decode(
        RawFramePacket packet,
        ushort[] transportRawCounts,
        int width,
        int height,
        ThermometryParams parameters,
        CameraThermometryStateSnapshot state,
        NativeCalibrationState calibrationState)
    {
        if (state.AlgorithmMode == ThermometryAlgorithmMode.Legacy)
        {
            return CreateLegacyFallbackResult(
                transportRawCounts,
                width,
                height,
                parameters,
                state,
                "legacy selected");
        }

        if (FrameTailParser.TryParseNativeCalibration(packet, state, out var calibration))
        {
            if (calibrationState.TryCreateCalibratedRaw(transportRawCounts, width, height, state, out var calibratedRawCounts))
            {
                return CreateNativeResult(
                    calibratedRawCounts,
                    width,
                    height,
                    parameters,
                    state,
                    calibration,
                    "calibrated raw");
            }

            if (NativeThermometryCore.CanUseLookup(transportRawCounts))
            {
                return CreateNativeResult(
                    transportRawCounts,
                    width,
                    height,
                    parameters,
                    state,
                    calibration,
                    calibrationState.GetDirectTransportReason());
            }

            return CreateLegacyFallbackResult(
                transportRawCounts,
                width,
                height,
                parameters,
                state,
                CreateNativeUnavailableReason("transport raw exceeds native 14-bit LUT range"));
        }

        return CreateLegacyFallbackResult(
            transportRawCounts,
            width,
            height,
            parameters,
            state,
            CreateNativeUnavailableReason(GetCalibrationFallbackReason(packet)));
    }

    private static TemperatureDecodeResult CreateNativeResult(
        ushort[] rawCounts,
        int width,
        int height,
        ThermometryParams parameters,
        CameraThermometryStateSnapshot state,
        NativeTailCalibration calibration,
        string reason)
    {
        return new TemperatureDecodeResult(
            rawCounts,
            NativeThermometryCore.Decode(rawCounts, width, height, parameters, state, calibration),
            ThermometryDecodeInfo.CreateNativeLookup(reason));
    }

    private static TemperatureDecodeResult CreateLegacyFallbackResult(
        ushort[] rawCounts,
        int width,
        int height,
        ThermometryParams parameters,
        CameraThermometryStateSnapshot state,
        string reason)
    {
        return new TemperatureDecodeResult(
            rawCounts,
            LegacyThermometryCore.Decode(rawCounts, width, height, parameters, state),
            ThermometryDecodeInfo.CreateLegacyFallback(reason));
    }

    private static string GetCalibrationFallbackReason(RawFramePacket packet)
    {
        if (packet.Width != 256)
        {
            return $"native LUT unsupported for width {packet.Width}";
        }

        return "native tail calibration unavailable";
    }

    private static string CreateNativeUnavailableReason(string detail) =>
        $"native selected unavailable: {detail}";
}

internal sealed record TemperatureDecodeResult(
    ushort[] SearchRawCounts,
    float[] Temperatures,
    ThermometryDecodeInfo DecodeInfo);

internal static class NativeThermometryCore
{
    private const int LookupLength = 1 << 14;

    public static bool CanUseLookup(ReadOnlySpan<ushort> rawCounts)
    {
        foreach (var raw in rawCounts)
        {
            if (raw >= LookupLength)
            {
                return false;
            }
        }

        return true;
    }

    public static float[] Decode(
        ushort[] rawCounts,
        int width,
        int height,
        ThermometryParams parameters,
        CameraThermometryStateSnapshot state,
        NativeTailCalibration calibration)
    {
        if (rawCounts.Length != width * height)
        {
            throw new ArgumentException("Raw count length does not match the expected thermal dimensions.", nameof(rawCounts));
        }

        var lookupTable = NativeThermometryMath.BuildLookupTable(parameters, state, calibration);
        var temperatures = new float[rawCounts.Length];

        for (var i = 0; i < rawCounts.Length; i++)
        {
            var lookupIndex = Math.Min((int)rawCounts[i], LookupLength - 1);
            temperatures[i] = parameters.Fix + lookupTable[lookupIndex];
        }

        return temperatures;
    }
}

internal static class NativeThermometryMath
{
    private const float KelvinOffset = 273.15f;
    private const float AtmosphericEpsilon = 1e-6f;
    private const int LookupLength = 1 << 14;

    public static float[] BuildLookupTable(
        ThermometryParams parameters,
        CameraThermometryStateSnapshot state,
        NativeTailCalibration calibration)
    {
        var ambientTemp = parameters.AmbientTemp;
        var reflectedTemp = parameters.ReflectedTemp;
        var emissivity = Math.Clamp(parameters.Emissivity, 0.01f, 1f);
        var humidity = NormalizeHumidity(parameters.Humidity);
        var distance = Math.Max(parameters.Distance, 0);
        var shutterTemp = calibration.ShutterTemp + state.ShutterFix;
        var waterVapor = humidity * MathF.Exp(
            1.5587f
            + (0.06939f * ambientTemp)
            - (0.00027816f * ambientTemp * ambientTemp)
            + (6.8455e-7f * ambientTemp * ambientTemp * ambientTemp));
        var sqrtWaterVapor = MathF.Sqrt(Math.Max(waterVapor, 0f));
        var sqrtShutterTemp = MathF.Sqrt(Math.Max(calibration.ShutterTemp, 0f));
        var tau = CalculateAtmosphericTau(sqrtShutterTemp, sqrtWaterVapor);
        var eta = Math.Max(emissivity * tau, AtmosphericEpsilon);
        var inverseEta = 1f / eta;
        var environmentalRadiance =
            ((1f - emissivity) * tau * Pow4(reflectedTemp + KelvinOffset))
            + ((1f - tau) * Pow4(ambientTemp + KelvinOffset));
        var curve = (calibration.A * shutterTemp * shutterTemp) + (calibration.B * shutterTemp);
        var slope =
            (calibration.Ka * calibration.FpaTemp * calibration.FpaTemp)
            + (calibration.Kb * calibration.FpaTemp)
            + calibration.Kc;
        var rawZero = calibration.RawBase - NativeThermometrySearch.GetFixOffset(state.RangeMode, calibration.Width, calibration.FpaTemp);
        var distanceFactor = CalculateDistanceFactor(state.CameraLens, distance);
        var discriminantBias = (calibration.B * calibration.B) / (4f * calibration.A * calibration.A);
        var rootBias = calibration.B / (2f * calibration.A);
        var lookupTable = new float[LookupLength];

        for (var raw = 0; raw < LookupLength; raw++)
        {
            var quadratic =
                discriminantBias
                + ((curve + (slope * (raw - rawZero))) / calibration.A);

            if (quadratic < 0f)
            {
                continue;
            }

            var surfaceKelvin = MathF.Sqrt(quadratic) - rootBias + KelvinOffset;
            if (surfaceKelvin <= 0f)
            {
                continue;
            }

            var radiance = (Pow4(surfaceKelvin) - environmentalRadiance) * inverseEta;
            if (radiance < 0f)
            {
                continue;
            }

            var sceneTemp = MathF.Pow(radiance, 0.25f) - KelvinOffset;
            lookupTable[raw] =
                calibration.CalibrateCorrect
                + sceneTemp
                + ((distanceFactor * (sceneTemp - ambientTemp)) / 100f);
        }

        return lookupTable;
    }

    private static float NormalizeHumidity(float humidity)
    {
        var clamped = Math.Max(humidity, 0f);
        return clamped > 1.5f ? clamped / 100f : clamped;
    }

    private static float CalculateAtmosphericTau(float sqrtShutterTemp, float sqrtWaterVapor)
    {
        var firstTerm =
            1.9f * MathF.Exp(-sqrtShutterTemp * (0.006569f - (0.002276f * sqrtWaterVapor)));
        var secondTerm =
            -0.9f * MathF.Exp(-sqrtShutterTemp * (0.01262f - (0.00667f * sqrtWaterVapor)));

        return firstTerm + secondTerm;
    }

    private static float CalculateDistanceFactor(int cameraLens, int distance)
    {
        if (cameraLens == CameraThermometryState.DefaultCameraLens)
        {
            return (0.85f * Math.Min(distance * 3f, 60f)) + 1.125f;
        }

        return (0.85f * Math.Min(distance, 20f)) - 1.125f;
    }

    private static float Pow4(float value)
    {
        var square = value * value;
        return square * square;
    }
}

internal static class NativeThermometrySearch
{
    public static int GetFixOffset(int rangeMode, int width, float fpaTemp)
    {
        if (rangeMode != CameraThermometryState.NormalRangeMode)
        {
            return 0;
        }

        var computed = Math.Max((int)((fpaTemp * -7.05f) + 390f), 0);
        if (width == 256 && computed == 0)
        {
            return 0xAA;
        }

        return computed;
    }
}

internal sealed class NativeCalibrationState
{
    private const int MaxBadPixelRepairs = 64;
    private const int ShutterFrameTarget = 6;
    private readonly object _sync = new();
    private readonly Queue<NativeCalibrationEvent> _eventQueue = new();

    private CaptureSourceMode _sourceMode = CaptureSourceMode.Raw;
    private bool _pendingKTableCapture;
    private bool _pendingShutterCapture;
    private ushort[]? _gainMap;
    private ushort[]? _shutterMap;
    private BadPixelRepair[] _badPixelRepairs = [];
    private long[]? _shutterAccumulator;
    private int _shutterCaptureFrames;
    private int _width;
    private int _height;

    public void UpdateSourceMode(CaptureSourceMode mode)
    {
        lock (_sync)
        {
            _sourceMode = mode;
        }
    }

    public void BeginKTableCapture()
    {
        lock (_sync)
        {
            _pendingKTableCapture = true;
        }
    }

    public void BeginShutterCapture()
    {
        lock (_sync)
        {
            _pendingShutterCapture = true;
            _shutterAccumulator = null;
            _shutterCaptureFrames = 0;
        }
    }

    public bool TryDequeueCalibrationEvent(out NativeCalibrationEvent calibrationEvent)
    {
        lock (_sync)
        {
            if (_eventQueue.Count == 0)
            {
                calibrationEvent = default;
                return false;
            }

            calibrationEvent = _eventQueue.Dequeue();
            return true;
        }
    }

    public void ObserveFrame(ushort[] rawCounts, int width, int height)
    {
        lock (_sync)
        {
            if (_pendingKTableCapture && _sourceMode == CaptureSourceMode.KTable)
            {
                _gainMap = rawCounts.ToArray();
                _badPixelRepairs = BuildBadPixelRepairs(_gainMap, width, height);
                _width = width;
                _height = height;
                _pendingKTableCapture = false;

                _eventQueue.Enqueue(new NativeCalibrationEvent(
                    NativeCalibrationKind.KTable,
                    $"Captured native K table ({_badPixelRepairs.Length} bad-pixel repair slot(s))."));
            }

            if (_pendingShutterCapture && _sourceMode == CaptureSourceMode.Raw)
            {
                if (_shutterAccumulator is null || _shutterAccumulator.Length != rawCounts.Length)
                {
                    _shutterAccumulator = new long[rawCounts.Length];
                    _shutterCaptureFrames = 0;
                }

                for (var i = 0; i < rawCounts.Length; i++)
                {
                    _shutterAccumulator[i] += rawCounts[i];
                }

                _shutterCaptureFrames++;
                if (_shutterCaptureFrames >= ShutterFrameTarget)
                {
                    _shutterMap = new ushort[rawCounts.Length];
                    for (var i = 0; i < rawCounts.Length; i++)
                    {
                        _shutterMap[i] = (ushort)(_shutterAccumulator[i] / _shutterCaptureFrames);
                    }

                    _width = width;
                    _height = height;
                    _pendingShutterCapture = false;
                    _shutterAccumulator = null;
                    _shutterCaptureFrames = 0;

                    _eventQueue.Enqueue(new NativeCalibrationEvent(
                        NativeCalibrationKind.Shutter,
                        $"Captured shutter reference from {ShutterFrameTarget} raw frame(s)."));
                }
            }
        }
    }

    public bool TryCreateCalibratedRaw(
        ushort[] transportRawCounts,
        int width,
        int height,
        CameraThermometryStateSnapshot state,
        out ushort[] calibratedRawCounts)
    {
        ushort[]? gainMap;
        ushort[]? shutterMap;
        BadPixelRepair[] badPixelRepairs;

        lock (_sync)
        {
            if (_gainMap is null ||
                _shutterMap is null ||
                _width != width ||
                _height != height ||
                _gainMap.Length != transportRawCounts.Length ||
                _shutterMap.Length != transportRawCounts.Length)
            {
                calibratedRawCounts = Array.Empty<ushort>();
                return false;
            }

            gainMap = _gainMap;
            shutterMap = _shutterMap;
            badPixelRepairs = _badPixelRepairs;
        }

        calibratedRawCounts = NativeRawPreprocessor.Apply(
            transportRawCounts,
            gainMap,
            shutterMap,
            badPixelRepairs,
            state.RangeMode == CameraThermometryState.WideRangeMode);
        return true;
    }

    public string GetDirectTransportReason()
    {
        lock (_sync)
        {
            if (_gainMap is not null && _shutterMap is null)
            {
                return "direct transport raw; waiting for shutter reference";
            }

            if (_gainMap is null && _shutterMap is not null)
            {
                return "direct transport raw; waiting for K table";
            }

            if (_pendingKTableCapture || _pendingShutterCapture)
            {
                return "direct transport raw; calibration pending";
            }

            return "direct transport raw";
        }
    }

    private static BadPixelRepair[] BuildBadPixelRepairs(ReadOnlySpan<ushort> gainMap, int width, int height)
    {
        var repairs = new List<BadPixelRepair>(MaxBadPixelRepairs);

        for (var index = 0; index < gainMap.Length && repairs.Count < MaxBadPixelRepairs; index++)
        {
            if (gainMap[index] != 0)
            {
                continue;
            }

            var x = index % width;
            var y = index / width;
            var neighbors = new List<int>(8);

            for (var deltaY = -1; deltaY <= 1; deltaY++)
            {
                for (var deltaX = -1; deltaX <= 1; deltaX++)
                {
                    if (deltaX == 0 && deltaY == 0)
                    {
                        continue;
                    }

                    var neighborX = x + deltaX;
                    var neighborY = y + deltaY;
                    if (neighborX < 0 || neighborX >= width || neighborY < 0 || neighborY >= height)
                    {
                        continue;
                    }

                    var neighborIndex = (neighborY * width) + neighborX;
                    if (gainMap[neighborIndex] == 0)
                    {
                        continue;
                    }

                    neighbors.Add(neighborIndex);
                }
            }

            if (neighbors.Count == 0)
            {
                continue;
            }

            repairs.Add(new BadPixelRepair(index, neighbors.ToArray()));
        }

        return repairs.ToArray();
    }
}

internal static class NativeRawPreprocessor
{
    public static ushort[] Apply(
        ushort[] transportRawCounts,
        ushort[] gainMap,
        ushort[] shutterMap,
        ReadOnlySpan<BadPixelRepair> badPixelRepairs,
        bool useWideBaseLevel)
    {
        var calibrated = new ushort[transportRawCounts.Length];
        var baseLevel = useWideBaseLevel ? 2000 : 6000;

        for (var i = 0; i < transportRawCounts.Length; i++)
        {
            var delta = ((short)transportRawCounts[i]) - ((short)shutterMap[i]);
            var gain = (short)gainMap[i];
            var adjusted = (baseLevel + ((delta * gain) >> 14)) & 0x3FFF;
            calibrated[i] = (ushort)adjusted;
        }

        foreach (var repair in badPixelRepairs)
        {
            var sum = 0;
            var count = 0;

            foreach (var neighborIndex in repair.NeighborIndices)
            {
                sum += calibrated[neighborIndex];
                count++;
            }

            if (count > 0)
            {
                calibrated[repair.Index] = (ushort)(sum / count);
            }
        }

        return calibrated;
    }
}

internal readonly record struct BadPixelRepair(
    int Index,
    int[] NeighborIndices);

internal static class LegacyThermometryCore
{
    public static float[] Decode(
        ushort[] rawCounts,
        int width,
        int height,
        ThermometryParams parameters,
        CameraThermometryStateSnapshot state)
    {
        if (rawCounts.Length != width * height)
        {
            throw new ArgumentException("Raw count length does not match the expected thermal dimensions.", nameof(rawCounts));
        }

        var statistics = RawCountStatistics.From(rawCounts);
        var profile = RangeProfile.From(state.RangeMode, state.CameraLens);
        var temperatures = new float[rawCounts.Length];

        var referenceCount = (float)(statistics.Min + (statistics.Span * profile.ReferenceFraction));
        var countsPerDegree = Math.Max(profile.CountsPerDegree, 1f);

        for (var i = 0; i < rawCounts.Length; i++)
        {
            var raw = rawCounts[i];
            var sceneTemp = parameters.AmbientTemp + ((raw - referenceCount) / countsPerDegree);
            sceneTemp = ThermometryMath.ApplyThermFix(sceneTemp, parameters, state, profile);
            sceneTemp = ThermometryMath.ApplyDistanceFix(sceneTemp, parameters, state, profile);
            temperatures[i] = Math.Clamp(sceneTemp, profile.MinimumClamp, profile.MaximumClamp);
        }

        return temperatures;
    }
}

internal static class SearchCore
{
    public static TemperatureSearchResult Search(float[] temperatures, ushort[] rawCounts, int width, int height)
    {
        if (temperatures.Length != width * height)
        {
            throw new ArgumentException("Temperature array length does not match the expected thermal dimensions.", nameof(temperatures));
        }

        if (rawCounts.Length != temperatures.Length)
        {
            throw new ArgumentException("Raw count array length does not match the temperature array length.", nameof(rawCounts));
        }

        var centerX = width / 2;
        var centerY = height / 2;
        var centerIndex = (centerY * width) + centerX;

        var maxIndex = 0;
        var minIndex = 0;

        for (var i = 1; i < temperatures.Length; i++)
        {
            if (temperatures[i] > temperatures[maxIndex])
            {
                maxIndex = i;
            }

            if (temperatures[i] < temperatures[minIndex])
            {
                minIndex = i;
            }
        }

        var maxPoint = new Point(maxIndex % width, maxIndex / width);
        var minPoint = new Point(minIndex % width, minIndex / width);

        var header = new float[TemperatureSearchResult.HeaderLength];
        header[0] = temperatures[centerIndex];
        header[1] = maxPoint.X;
        header[2] = maxPoint.Y;
        header[3] = temperatures[maxIndex];
        header[4] = minPoint.X;
        header[5] = minPoint.Y;
        header[6] = temperatures[minIndex];
        header[7] = rawCounts[maxIndex];
        header[8] = rawCounts[minIndex];
        header[9] = 0f;

        return new TemperatureSearchResult(header, maxPoint, minPoint, rawCounts[maxIndex], rawCounts[minIndex]);
    }
}

internal static class ThermometryMath
{
    public static float ApplyThermFix(
        float sceneTemp,
        ThermometryParams parameters,
        CameraThermometryStateSnapshot state,
        RangeProfile profile)
    {
        var emissivity = Math.Clamp(parameters.Emissivity, 0.1f, 1f);
        var reflectedBlend = parameters.ReflectedTemp * (1f - emissivity);
        var corrected = ((sceneTemp - reflectedBlend) / emissivity)
            + parameters.Fix
            + (state.ShutterFix * profile.ShutterGain)
            + profile.BaseOffset;

        return corrected;
    }

    public static float ApplyDistanceFix(
        float sceneTemp,
        ThermometryParams parameters,
        CameraThermometryStateSnapshot state,
        RangeProfile profile)
    {
        var humidity = Math.Clamp(parameters.Humidity, 0f, 100f);
        var distanceMeters = Math.Max(parameters.Distance, 0);
        var humidityLoss = (humidity / 100f) * profile.HumidityPenaltyPerMeter;
        var lensFactor = state.CameraLens == CameraThermometryState.AlternateCameraLens ? 1.15f : 1f;
        var distancePenalty = distanceMeters * profile.DistancePenaltyPerMeter * lensFactor;

        return sceneTemp - distancePenalty + humidityLoss;
    }
}

internal readonly record struct RangeProfile(
    float CountsPerDegree,
    float ReferenceFraction,
    float BaseOffset,
    float ShutterGain,
    float DistancePenaltyPerMeter,
    float HumidityPenaltyPerMeter,
    float MinimumClamp,
    float MaximumClamp)
{
    public static RangeProfile From(int rangeMode, int cameraLens)
    {
        var lensScale = cameraLens == CameraThermometryState.AlternateCameraLens ? 0.85f : 1f;

        return rangeMode switch
        {
            CameraThermometryState.WideRangeMode => new RangeProfile(
                CountsPerDegree: 9f * lensScale,
                ReferenceFraction: 0.08f,
                BaseOffset: 120f,
                ShutterGain: 0.35f,
                DistancePenaltyPerMeter: 0.018f,
                HumidityPenaltyPerMeter: 0.006f,
                MinimumClamp: 100f,
                MaximumClamp: 450f),
            _ => new RangeProfile(
                CountsPerDegree: 36f * lensScale,
                ReferenceFraction: 0.15f,
                BaseOffset: 0f,
                ShutterGain: 0.2f,
                DistancePenaltyPerMeter: 0.01f,
                HumidityPenaltyPerMeter: 0.003f,
                MinimumClamp: -20f,
                MaximumClamp: 140f)
        };
    }
}

internal readonly record struct RawCountStatistics(double Min, double Max, double Mean)
{
    public double Span => Math.Max(Max - Min, 1d);

    public static RawCountStatistics From(ReadOnlySpan<ushort> rawCounts)
    {
        if (rawCounts.Length == 0)
        {
            throw new ArgumentException("At least one raw count is required.", nameof(rawCounts));
        }

        double min = rawCounts[0];
        double max = rawCounts[0];
        double sum = 0;

        foreach (var raw in rawCounts)
        {
            if (raw < min)
            {
                min = raw;
            }

            if (raw > max)
            {
                max = raw;
            }

            sum += raw;
        }

        return new RawCountStatistics(min, max, sum / rawCounts.Length);
    }
}
