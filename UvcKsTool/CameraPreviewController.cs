using System.Runtime.InteropServices;
using OpenCvSharp;

namespace UvcKsTool;

internal sealed class CameraPreviewController : IDisposable
{
    private const int ExpectedWidth = 256;
    private const int ExpectedHeight = 196;
    private const int ExpectedBytes = ExpectedWidth * ExpectedHeight * sizeof(ushort);
    private static readonly CaptureCandidate[] CaptureCandidates =
    [
        new(VideoCaptureAPIs.MSMF, "Media Foundation", null),
        new(VideoCaptureAPIs.MSMF, "Media Foundation + Y16 ", MakeFourCc('Y', '1', '6', ' ')),
        new(VideoCaptureAPIs.DSHOW, "DirectShow", null),
        new(VideoCaptureAPIs.DSHOW, "DirectShow + Y16 ", MakeFourCc('Y', '1', '6', ' ')),
        new(VideoCaptureAPIs.DSHOW, "DirectShow + YUY2", MakeFourCc('Y', 'U', 'Y', '2')),
        new(VideoCaptureAPIs.ANY, "OpenCV default", null)
    ];

    private readonly PictureBox _pictureBox;
    private readonly IThermometryDecoder _decoder;
    private readonly object _sync = new();
    private readonly CameraThermometryState _state = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private ThermometryParams _parameters = ThermometryParams.Default;
    private bool _parametersSeeded;
    private RawFramePacket? _latestPacket;
    private RadiometricFrame? _latestFrame;
    private int _paletteIndex;

    public CameraPreviewController(PictureBox pictureBox, IThermometryDecoder? decoder = null)
    {
        _pictureBox = pictureBox;
        _decoder = decoder ?? new ManagedThermometryDecoder();
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _loopTask is { IsCompleted: false };
            }
        }
    }

    public event Action<string>? StatusChanged;

    public event Action<RadiometricFrame>? FrameDecoded;

    public event Action<NativeCalibrationEvent>? CalibrationCaptured;

    public void Start(int deviceIndex)
    {
        Stop();

        var cts = new CancellationTokenSource();
        Task loopTask;

        try
        {
            loopTask = Task.Run(() => OpenAndCaptureLoop(deviceIndex, cts.Token), cts.Token);
        }
        catch
        {
            cts.Dispose();
            throw;
        }

        lock (_sync)
        {
            _cts = cts;
            _loopTask = loopTask;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loopTask;

        lock (_sync)
        {
            cts = _cts;
            loopTask = _loopTask;
            _cts = null;
            _loopTask = null;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }
        }

        if (loopTask is not null)
        {
            try
            {
                loopTask.Wait(TimeSpan.FromMilliseconds(1200));
            }
            catch
            {
            }
        }

        cts?.Dispose();
        ClearPreview();
    }

    public void Dispose()
    {
        Stop();
    }

    public void UpdateThermometryParams(ThermometryParams parameters)
    {
        lock (_sync)
        {
            _parameters = parameters;
            _parametersSeeded = true;
        }

        _state.RequestRefresh();
    }

    public void UpdateRangeMode(int rangeMode) => _state.UpdateRangeMode(rangeMode);

    public void UpdateCameraLens(int cameraLens) => _state.UpdateCameraLens(cameraLens);

    public void UpdateAlgorithmMode(ThermometryAlgorithmMode algorithmMode) => _state.UpdateAlgorithmMode(algorithmMode);

    public void UpdateShutterFix(float shutterFix) => _state.UpdateShutterFix(shutterFix);

    public void UpdateSourceMode(CaptureSourceMode mode)
    {
        if (_decoder is INativeCalibrationController calibrationController)
        {
            calibrationController.UpdateSourceMode(mode);
        }
    }

    public void BeginKTableCapture()
    {
        if (_decoder is INativeCalibrationController calibrationController)
        {
            calibrationController.BeginKTableCapture();
        }
    }

    public void BeginShutterCapture()
    {
        if (_decoder is INativeCalibrationController calibrationController)
        {
            calibrationController.BeginShutterCapture();
        }
    }

    public CameraThermometryStateSnapshot GetStateSnapshot() => _state.Snapshot();

    public void RequestShutterRefresh() => _state.RequestRefresh();

    public void UpdatePalette(int paletteIndex)
    {
        lock (_sync)
        {
            _paletteIndex = paletteIndex;
        }
    }

    public string ExportLatestCapture(string rootDirectory)
    {
        RawFramePacket? packet;
        RadiometricFrame? frame;

        lock (_sync)
        {
            packet = _latestPacket;
            frame = _latestFrame;
        }

        if (packet is null || frame is null)
        {
            throw new InvalidOperationException("No decoded radiometric frame is available yet.");
        }

        var outputDirectory = Path.Combine(rootDirectory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}");
        return DebugCaptureExporter.Export(packet, frame, outputDirectory);
    }

    private static CaptureSession OpenCapture(int deviceIndex)
    {
        var failures = new List<string>();

        foreach (var candidate in CaptureCandidates)
        {
            var capture = new VideoCapture(deviceIndex, candidate.Api);
            if (!capture.IsOpened())
            {
                capture.Dispose();
                failures.Add($"{candidate.Description}: open failed");
                continue;
            }

            try
            {
                ConfigureCapture(capture, candidate);

                using var probeFrame = new Mat();
                if (!TryReadRawProbeFrame(capture, probeFrame, out var description))
                {
                    failures.Add($"{candidate.Description}: {description}");
                    capture.Release();
                    capture.Dispose();
                    continue;
                }

                return new CaptureSession(capture, candidate.Description);
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate.Description}: {ex.Message}");
                capture.Release();
                capture.Dispose();
            }
        }

        throw new InvalidOperationException(
            $"Could not open a raw transport stream on camera index {deviceIndex}. " +
            $"Tried {CaptureCandidates.Length} candidate(s): {string.Join("; ", failures)}");
    }

    private static void ConfigureCapture(VideoCapture capture, CaptureCandidate candidate)
    {
        capture.Set(VideoCaptureProperties.BufferSize, 1);
        capture.Set(VideoCaptureProperties.FrameWidth, ExpectedWidth);
        capture.Set(VideoCaptureProperties.FrameHeight, ExpectedHeight);
        capture.Set(VideoCaptureProperties.ConvertRgb, 0);
        capture.Set(VideoCaptureProperties.Format, -1);

        if (candidate.FourCc is not null)
        {
            capture.Set(VideoCaptureProperties.FourCC, candidate.FourCc.Value);
        }
    }

    private static int MakeFourCc(char c1, char c2, char c3, char c4) =>
        c1 | (c2 << 8) | (c3 << 16) | (c4 << 24);

    private static bool TryReadRawProbeFrame(VideoCapture capture, Mat probeFrame, out string description)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!capture.Read(probeFrame) || probeFrame.Empty())
            {
                Thread.Sleep(20);
                continue;
            }

            if (TryBuildRawFramePacket(probeFrame, out _, out var probeDescription))
            {
                description = probeDescription;
                return true;
            }

            description = probeDescription;
            return false;
        }

        description = "timed out waiting for a frame";
        return false;
    }

    private void OpenAndCaptureLoop(int deviceIndex, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            StatusChanged?.Invoke($"Opening measurement stream on camera index {deviceIndex}...");

            var session = OpenCapture(deviceIndex);
            if (token.IsCancellationRequested)
            {
                session.Capture.Release();
                session.Capture.Dispose();
                StatusChanged?.Invoke("Measurement stopped.");
                return;
            }

            StatusChanged?.Invoke($"Measurement started on camera index {deviceIndex} via {session.Description}.");
            CaptureLoop(session.Capture, token);
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke("Measurement stopped.");
        }
        catch (Exception ex)
        {
            ClearPreview();
            StatusChanged?.Invoke($"Measurement failed: {ex.Message}");
            StatusChanged?.Invoke("Measurement stopped.");
        }
    }

    private void CaptureLoop(VideoCapture capture, CancellationToken token)
    {
        using var rawFrame = new Mat();
        var publishedFormat = false;
        var publishedEmbeddedParameters = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!capture.Read(rawFrame) || rawFrame.Empty())
                {
                    Thread.Sleep(30);
                    continue;
                }

                if (!TryBuildRawFramePacket(rawFrame, out var packet, out var frameDescription))
                {
                    throw new InvalidOperationException(frameDescription);
                }

                var activeParameters = ResolveActiveParameters(packet);
                var decodedFrame = _decoder.Decode(packet, activeParameters, _state);
                var previewBitmap = TemperaturePreviewRenderer.Render(decodedFrame, GetPaletteIndex());

                lock (_sync)
                {
                    _latestPacket = packet;
                    _latestFrame = decodedFrame;
                }

                PublishBitmap(previewBitmap);
                FrameDecoded?.Invoke(decodedFrame);
                DrainCalibrationEvents();

                if (!publishedFormat)
                {
                    publishedFormat = true;
                    StatusChanged?.Invoke(
                        $"Raw frame transport: {frameDescription}, logical={packet.Width}x{packet.Height}, bytes={packet.RawBytes.Length}.");
                }

                if (!publishedEmbeddedParameters)
                {
                    publishedEmbeddedParameters = true;
                    var productVersion = string.IsNullOrWhiteSpace(decodedFrame.TailMetadata.ProductVersion)
                        ? "<unavailable>"
                        : decodedFrame.TailMetadata.ProductVersion;
                    StatusChanged?.Invoke(
                        $"Embedded params: fix={decodedFrame.TailMetadata.EmbeddedParameters.Fix:F2}, refl={decodedFrame.TailMetadata.EmbeddedParameters.ReflectedTemp:F1}, " +
                        $"air={decodedFrame.TailMetadata.EmbeddedParameters.AmbientTemp:F1}, humi={decodedFrame.TailMetadata.EmbeddedParameters.Humidity:F1}, " +
                        $"emiss={decodedFrame.TailMetadata.EmbeddedParameters.Emissivity:F2}, dist={decodedFrame.TailMetadata.EmbeddedParameters.Distance}, version={productVersion}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Measurement failed: {ex.Message}");
        }
        finally
        {
            capture.Release();
            capture.Dispose();
            ClearPreview();
            StatusChanged?.Invoke("Measurement stopped.");
        }
    }

    private void DrainCalibrationEvents()
    {
        if (_decoder is not INativeCalibrationController calibrationController)
        {
            return;
        }

        while (calibrationController.TryDequeueCalibrationEvent(out var calibrationEvent))
        {
            StatusChanged?.Invoke(calibrationEvent.Message);
            CalibrationCaptured?.Invoke(calibrationEvent);
        }
    }

    private int GetPaletteIndex()
    {
        lock (_sync)
        {
            return _paletteIndex;
        }
    }

    private ThermometryParams ResolveActiveParameters(RawFramePacket packet)
    {
        lock (_sync)
        {
            if (_parametersSeeded)
            {
                return _parameters;
            }
        }

        var embedded = FrameTailParser.ParseMetadata(packet).EmbeddedParameters;

        lock (_sync)
        {
            if (!_parametersSeeded)
            {
                _parameters = embedded;
                _parametersSeeded = true;
            }

            return _parameters;
        }
    }

    private static bool TryBuildRawFramePacket(Mat source, out RawFramePacket packet, out string description)
    {
        packet = null!;

        if (source.Empty())
        {
            description = "empty frame";
            return false;
        }

        var totalBytes = checked((int)(source.Total() * source.ElemSize()));
        var rowBytes = checked((int)(source.Width * source.ElemSize()));
        var rawBytes = new byte[totalBytes];

        var rows = source.Height;
        for (var y = 0; y < rows; y++)
        {
            var srcRow = new IntPtr(source.Data + (y * (long)source.Step()));
            Marshal.Copy(srcRow, rawBytes, y * rowBytes, rowBytes);
        }

        if (rawBytes.Length != ExpectedBytes)
        {
            description =
                $"expected {ExpectedWidth}x{ExpectedHeight}x16-bit transport ({ExpectedBytes} bytes), " +
                $"got {source.Width}x{source.Height}, type={source.Type()}, bytes={rawBytes.Length}";
            return false;
        }

        packet = new RawFramePacket(
            ExpectedWidth,
            ExpectedHeight,
            DateTimeOffset.Now,
            rawBytes,
            RawTransportFormat.UInt16LittleEndian);

        description = $"source {source.Width}x{source.Height}, type={source.Type()}, bytes={rawBytes.Length}";
        return true;
    }

    private void PublishBitmap(Bitmap bitmap)
    {
        if (_pictureBox.IsDisposed)
        {
            bitmap.Dispose();
            return;
        }

        void Apply()
        {
            var previous = _pictureBox.Image;
            _pictureBox.Image = bitmap;
            previous?.Dispose();
        }

        if (_pictureBox.InvokeRequired)
        {
            try
            {
                _pictureBox.BeginInvoke((MethodInvoker)Apply);
            }
            catch
            {
                bitmap.Dispose();
            }
        }
        else
        {
            Apply();
        }
    }

    private void ClearPreview()
    {
        if (_pictureBox.IsDisposed)
        {
            return;
        }

        void Clear()
        {
            var previous = _pictureBox.Image;
            _pictureBox.Image = null;
            previous?.Dispose();
        }

        if (_pictureBox.InvokeRequired)
        {
            try
            {
                _pictureBox.BeginInvoke((MethodInvoker)Clear);
            }
            catch
            {
            }
        }
        else
        {
            Clear();
        }
    }

    private sealed record CaptureCandidate(VideoCaptureAPIs Api, string Description, int? FourCc);

    private sealed record CaptureSession(VideoCapture Capture, string Description);
}
