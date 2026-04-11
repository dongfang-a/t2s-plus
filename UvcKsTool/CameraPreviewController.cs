using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace UvcKsTool;

internal sealed class CameraPreviewController : IDisposable
{
    private readonly PictureBox _pictureBox;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private VideoCapture? _capture;

    public CameraPreviewController(PictureBox pictureBox)
    {
        _pictureBox = pictureBox;
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _capture is not null;
            }
        }
    }

    public event Action<string>? StatusChanged;

    public void Start(int deviceIndex)
    {
        Stop();

        var capture = OpenCapture(deviceIndex);
        var cts = new CancellationTokenSource();

        lock (_sync)
        {
            _capture = capture;
            _cts = cts;
            _loopTask = Task.Run(() => CaptureLoop(capture, cts.Token), cts.Token);
        }

        StatusChanged?.Invoke($"Preview started on camera index {deviceIndex}.");
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loopTask;
        VideoCapture? capture;

        lock (_sync)
        {
            cts = _cts;
            loopTask = _loopTask;
            capture = _capture;

            _cts = null;
            _loopTask = null;
            _capture = null;
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
                loopTask.Wait(TimeSpan.FromMilliseconds(800));
            }
            catch
            {
            }
        }

        capture?.Release();
        capture?.Dispose();
        cts?.Dispose();

        ClearPreview();
        StatusChanged?.Invoke("Preview stopped.");
    }

    public void Dispose()
    {
        Stop();
    }

    private static VideoCapture OpenCapture(int deviceIndex)
    {
        var capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.DSHOW);
        if (!capture.IsOpened())
        {
            capture.Dispose();
            capture = new VideoCapture(deviceIndex);
        }

        if (!capture.IsOpened())
        {
            capture.Dispose();
            throw new InvalidOperationException($"Could not open camera index {deviceIndex}.");
        }

        capture.Set(VideoCaptureProperties.BufferSize, 1);
        capture.Set(VideoCaptureProperties.ConvertRgb, 1);

        return capture;
    }

    private void CaptureLoop(VideoCapture capture, CancellationToken token)
    {
        using var rawFrame = new Mat();

        var publishedFrameInfo = false;

        while (!token.IsCancellationRequested)
        {
            if (!capture.Read(rawFrame) || rawFrame.Empty())
            {
                Thread.Sleep(30);
                continue;
            }

            using var displayFrame = NormalizeFrame(rawFrame, out var cropped);
            var bitmap = CreateBitmap(displayFrame);

            PublishBitmap(bitmap);

            if (!publishedFrameInfo)
            {
                publishedFrameInfo = true;
                var cropSuffix = cropped ? " (cropped to 256x192 preview)" : string.Empty;
                StatusChanged?.Invoke(
                    $"Frame format: {rawFrame.Width}x{rawFrame.Height}, channels={rawFrame.Channels()}{cropSuffix}.");
            }
        }
    }

    private static Mat NormalizeFrame(Mat source, out bool cropped)
    {
        cropped = false;

        Mat converted;
        switch (source.Channels())
        {
            case 4:
                converted = source.CvtColor(ColorConversionCodes.BGRA2BGR);
                break;
            case 3:
                converted = source.Clone();
                break;
            case 2:
                converted = source.CvtColor(ColorConversionCodes.YUV2BGR_YUY2);
                break;
            case 1:
                converted = source.CvtColor(ColorConversionCodes.GRAY2BGR);
                break;
            default:
                converted = source.Clone();
                break;
        }

        if (converted.Width == 256 && converted.Height == 196)
        {
            cropped = true;
            var croppedFrame = new Mat(converted, new Rect(0, 0, 256, 192)).Clone();
            converted.Dispose();
            return croppedFrame;
        }

        return converted;
    }

    private static Bitmap CreateBitmap(Mat source)
    {
        if (source.Type() != MatType.CV_8UC3)
        {
            throw new InvalidOperationException($"Preview expects CV_8UC3 frames, got {source.Type()}.");
        }

        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            bitmap.PixelFormat);

        try
        {
            var srcStride = (int)source.Step();
            var dstStride = data.Stride;
            var rowBytes = source.Width * source.ElemSize();

            var buffer = new byte[rowBytes];
            for (var y = 0; y < source.Height; y++)
            {
                var srcRow = source.Data + (y * srcStride);
                var dstRow = data.Scan0 + (y * dstStride);
                Marshal.Copy(srcRow, buffer, 0, buffer.Length);
                Marshal.Copy(buffer, 0, dstRow, buffer.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
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
}
