using System.Drawing;

namespace UvcKsTool;

internal enum RawTransportFormat
{
    Unknown = 0,
    UInt16LittleEndian = 1
}

internal sealed record RawFramePacket(
    int Width,
    int Height,
    DateTimeOffset Timestamp,
    byte[] RawBytes,
    RawTransportFormat TransportFormat);

internal sealed record ThermometryParams(
    float Fix,
    float ReflectedTemp,
    float AmbientTemp,
    float Humidity,
    float Emissivity,
    int Distance)
{
    public static ThermometryParams Default { get; } = new(0f, 20f, 20f, 50f, 0.95f, 1);
}

internal readonly record struct CameraThermometryStateSnapshot(
    int RangeMode,
    int CameraLens,
    float ShutterFix,
    bool Dirty);

internal sealed class CameraThermometryState
{
    public const int NormalRangeMode = 0x78;
    public const int WideRangeMode = 0x190;
    public const int DefaultCameraLens = 0x44;
    public const int AlternateCameraLens = 0x82;

    private readonly object _sync = new();

    private int _rangeMode = NormalRangeMode;
    private int _cameraLens = DefaultCameraLens;
    private float _shutterFix;
    private bool _dirty = true;

    public CameraThermometryStateSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new CameraThermometryStateSnapshot(_rangeMode, _cameraLens, _shutterFix, _dirty);
        }
    }

    public void UpdateRangeMode(int rangeMode)
    {
        lock (_sync)
        {
            _rangeMode = rangeMode;
            _dirty = true;
        }
    }

    public void UpdateCameraLens(int cameraLens)
    {
        lock (_sync)
        {
            _cameraLens = cameraLens;
            _dirty = true;
        }
    }

    public void UpdateShutterFix(float shutterFix)
    {
        lock (_sync)
        {
            _shutterFix = shutterFix;
            _dirty = true;
        }
    }

    public void RequestRefresh()
    {
        lock (_sync)
        {
            _dirty = true;
        }
    }

    public void MarkDecoded()
    {
        lock (_sync)
        {
            _dirty = false;
        }
    }
}

internal sealed record FrameTailMetadata(
    ThermometryParams EmbeddedParameters,
    string ProductVersion,
    byte[] ParameterBlock);

internal sealed record TemperatureSearchResult(
    float[] Header,
    Point MaxPoint,
    Point MinPoint,
    ushort MaxRawCount,
    ushort MinRawCount)
{
    public const int HeaderLength = 10;
}

internal sealed record RadiometricFrame(
    DateTimeOffset Timestamp,
    int ThermalWidth,
    int ThermalHeight,
    ushort[] RawCounts,
    float[] Temperatures,
    TemperatureSearchResult Search,
    ThermometryParams ActiveParameters,
    CameraThermometryStateSnapshot State,
    FrameTailMetadata TailMetadata)
{
    public float[] Header => Search.Header;
    public float CenterTemp => Header[0];
    public float MaxTemp => Header[3];
    public float MinTemp => Header[6];
    public Point MaxPoint => Search.MaxPoint;
    public Point MinPoint => Search.MinPoint;
}

internal interface IThermometryDecoder
{
    RadiometricFrame Decode(RawFramePacket packet, ThermometryParams parameters, CameraThermometryState state);
}
