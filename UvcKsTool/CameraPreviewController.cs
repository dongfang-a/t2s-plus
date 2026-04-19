using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace UvcKsTool;

internal sealed class CameraPreviewController : IDisposable
{
    private const int ExpectedWidth = 256;
    private const int ExpectedHeight = 196;
    private const int ExpectedBytes = ExpectedWidth * ExpectedHeight * sizeof(ushort);
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(40);
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
    private readonly LatestOnlyUiQueue<PendingPreviewUpdate> _previewPublishQueue = new();
    private readonly byte[][] _packetBuffers =
    [
        new byte[ExpectedBytes],
        new byte[ExpectedBytes]
    ];

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private ThermometryParams _parameters = ThermometryParams.Default;
    private bool _parametersSeeded;
    private RawFramePacket? _latestPacket;
    private RadiometricFrame? _latestFrame;
    private int _latestPacketBufferIndex = -1;
    private int _paletteIndex;
    private ReusablePreviewSurface[]? _previewSurfaces;
    private int _nextPreviewSurfaceIndex;
    private ReusablePreviewSurface? _displaySurface;
    private ReusablePreviewSurface? _applyingSurface;
    private ReusablePreviewSurface? _pendingSurface;

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

        lock (_sync)
        {
            DisposePreviewSurfacesUnsafe();
        }
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
            if (_latestPacket is null || _latestFrame is null)
            {
                throw new InvalidOperationException("No decoded radiometric frame is available yet.");
            }

            packet = new RawFramePacket(
                _latestPacket.Width,
                _latestPacket.Height,
                _latestPacket.Timestamp,
                (byte[])_latestPacket.RawBytes.Clone(),
                _latestPacket.TransportFormat);
            frame = _latestFrame;
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
        var probeBuffer = new byte[ExpectedBytes];

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!capture.Read(probeFrame) || probeFrame.Empty())
            {
                Thread.Sleep(20);
                continue;
            }

            if (TryBuildRawFramePacket(probeFrame, probeBuffer, out _, out var probeDescription))
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
        var scheduler = new PreviewFrameScheduler(PreviewInterval);
        var stopwatch = Stopwatch.StartNew();
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

                if (!scheduler.ShouldProcess(stopwatch.Elapsed))
                {
                    continue;
                }

                var packetBufferIndex = RentPacketBufferIndex();
                if (!TryBuildRawFramePacket(rawFrame, _packetBuffers[packetBufferIndex], out var packet, out var frameDescription))
                {
                    throw new InvalidOperationException(frameDescription);
                }

                var previewSurface = AcquirePreviewSurface(packet.Width, packet.Height - FrameTailParser.MetadataRows);
                if (previewSurface is null)
                {
                    continue;
                }

                var activeParameters = ResolveActiveParameters(packet);
                var decodedFrame = _decoder.Decode(packet, activeParameters, _state);
                TemperaturePreviewRenderer.RenderInto(previewSurface, decodedFrame, GetPaletteIndex());

                lock (_sync)
                {
                    _latestPacket = packet;
                    _latestFrame = decodedFrame;
                    _latestPacketBufferIndex = packetBufferIndex;
                }

                QueuePreviewUpdate(previewSurface, decodedFrame);
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

    private void QueuePreviewUpdate(ReusablePreviewSurface surface, RadiometricFrame frame)
    {
        lock (_sync)
        {
            _pendingSurface = surface;
        }

        var shouldSchedule = _previewPublishQueue.Enqueue(new PendingPreviewUpdate(surface, frame));
        if (!shouldSchedule)
        {
            return;
        }

        if (_pictureBox.IsDisposed)
        {
            _previewPublishQueue.Clear();
            return;
        }

        void Flush() => FlushPendingPreviewUpdates();

        if (_pictureBox.InvokeRequired)
        {
            try
            {
                _pictureBox.BeginInvoke((MethodInvoker)Flush);
            }
            catch
            {
                _previewPublishQueue.Clear();
            }
        }
        else
        {
            Flush();
        }
    }

    private void FlushPendingPreviewUpdates()
    {
        if (_pictureBox.IsDisposed)
        {
            _previewPublishQueue.Clear();
            return;
        }

        while (_previewPublishQueue.TryTakeLatest(out var update))
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pendingSurface, update.Surface))
                {
                    _pendingSurface = null;
                }

                _applyingSurface = update.Surface;
            }

            _pictureBox.Image = update.Surface.Bitmap;
            FrameDecoded?.Invoke(update.Frame);

            lock (_sync)
            {
                _displaySurface = update.Surface;
                _applyingSurface = null;
            }
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

    private int RentPacketBufferIndex()
    {
        lock (_sync)
        {
            return _latestPacketBufferIndex == 0 ? 1 : 0;
        }
    }

    private ReusablePreviewSurface? AcquirePreviewSurface(int width, int height)
    {
        lock (_sync)
        {
            EnsurePreviewSurfacesUnsafe(width, height);
            if (_previewSurfaces is null)
            {
                return null;
            }

            for (var i = 0; i < _previewSurfaces.Length; i++)
            {
                var index = (_nextPreviewSurfaceIndex + i) % _previewSurfaces.Length;
                var candidate = _previewSurfaces[index];
                if (ReferenceEquals(candidate, _displaySurface)
                    || ReferenceEquals(candidate, _applyingSurface)
                    || ReferenceEquals(candidate, _pendingSurface))
                {
                    continue;
                }

                _nextPreviewSurfaceIndex = (index + 1) % _previewSurfaces.Length;
                return candidate;
            }

            return null;
        }
    }

    private void EnsurePreviewSurfacesUnsafe(int width, int height)
    {
        if (_previewSurfaces is not null
            && _previewSurfaces.Length > 0
            && _previewSurfaces[0].Width == width
            && _previewSurfaces[0].Height == height)
        {
            return;
        }

        DisposePreviewSurfacesUnsafe();

        _previewSurfaces =
        [
            new ReusablePreviewSurface(width, height),
            new ReusablePreviewSurface(width, height),
            new ReusablePreviewSurface(width, height),
            new ReusablePreviewSurface(width, height)
        ];
        _nextPreviewSurfaceIndex = 0;
        _displaySurface = null;
        _applyingSurface = null;
        _pendingSurface = null;
    }

    private void DisposePreviewSurfacesUnsafe()
    {
        if (_previewSurfaces is null)
        {
            return;
        }

        foreach (var surface in _previewSurfaces)
        {
            surface.Dispose();
        }

        _previewSurfaces = null;
    }

    private static bool TryBuildRawFramePacket(Mat source, byte[] rawBytes, out RawFramePacket packet, out string description)
    {
        packet = null!;

        if (source.Empty())
        {
            description = "empty frame";
            return false;
        }

        var totalBytes = checked((int)(source.Total() * source.ElemSize()));
        if (totalBytes != ExpectedBytes)
        {
            description =
                $"expected {ExpectedWidth}x{ExpectedHeight}x16-bit transport ({ExpectedBytes} bytes), " +
                $"got {source.Width}x{source.Height}, type={source.Type()}, bytes={totalBytes}";
            return false;
        }

        var rowBytes = checked((int)(source.Width * source.ElemSize()));
        var rows = source.Height;
        for (var y = 0; y < rows; y++)
        {
            var srcRow = new IntPtr(source.Data + (y * (long)source.Step()));
            Marshal.Copy(srcRow, rawBytes, y * rowBytes, rowBytes);
        }

        packet = new RawFramePacket(
            ExpectedWidth,
            ExpectedHeight,
            DateTimeOffset.Now,
            rawBytes,
            RawTransportFormat.UInt16LittleEndian);

        description = $"source {source.Width}x{source.Height}, type={source.Type()}, bytes={totalBytes}";
        return true;
    }

    private void ClearPreview()
    {
        _previewPublishQueue.Clear();

        lock (_sync)
        {
            _displaySurface = null;
            _applyingSurface = null;
            _pendingSurface = null;
        }

        if (_pictureBox.IsDisposed)
        {
            return;
        }

        void Clear()
        {
            _pictureBox.Image = null;
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

    private sealed record PendingPreviewUpdate(
        ReusablePreviewSurface Surface,
        RadiometricFrame Frame);
}

internal sealed class PreviewFrameScheduler
{
    private readonly TimeSpan _interval;
    private TimeSpan _nextDue;
    private bool _started;

    public PreviewFrameScheduler(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _interval = interval;
    }

    public bool ShouldProcess(TimeSpan elapsed)
    {
        if (!_started)
        {
            _started = true;
            _nextDue = elapsed + _interval;
            return true;
        }

        if (elapsed < _nextDue)
        {
            return false;
        }

        _nextDue = elapsed + _interval;
        return true;
    }
}

internal sealed class LatestOnlyUiQueue<T>
{
    private readonly object _sync = new();

    private bool _invokeQueued;
    private bool _hasPending;
    private T? _pending;

    public bool HasInvokeQueued
    {
        get
        {
            lock (_sync)
            {
                return _invokeQueued;
            }
        }
    }

    public bool HasPendingUpdate
    {
        get
        {
            lock (_sync)
            {
                return _hasPending;
            }
        }
    }

    public bool Enqueue(T value)
    {
        lock (_sync)
        {
            _pending = value;
            _hasPending = true;

            if (_invokeQueued)
            {
                return false;
            }

            _invokeQueued = true;
            return true;
        }
    }

    public bool TryTakeLatest(out T value)
    {
        lock (_sync)
        {
            if (_hasPending)
            {
                value = _pending!;
                _pending = default;
                _hasPending = false;
                return true;
            }

            _invokeQueued = false;
            value = default!;
            return false;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _pending = default;
            _hasPending = false;
            _invokeQueued = false;
        }
    }
}
