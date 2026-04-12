namespace UvcKsTool;

internal sealed class ManagedThermometryDecoder : IThermometryDecoder
{
    public RadiometricFrame Decode(RawFramePacket packet, ThermometryParams parameters, CameraThermometryState state)
    {
        var snapshot = state.Snapshot();
        var tailMetadata = FrameTailParser.ParseMetadata(packet);
        var rawCounts = FrameTailParser.ExtractThermalCounts(packet);
        var thermalHeight = packet.Height - FrameTailParser.MetadataRows;
        var temperatures = ThermometryCore.Decode(rawCounts, packet.Width, thermalHeight, parameters, snapshot);
        var search = SearchCore.Search(temperatures, rawCounts, packet.Width, thermalHeight);

        state.MarkDecoded();

        return new RadiometricFrame(
            packet.Timestamp,
            packet.Width,
            thermalHeight,
            rawCounts,
            temperatures,
            search,
            parameters,
            state.Snapshot(),
            tailMetadata);
    }
}

internal static class ThermometryCore
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
