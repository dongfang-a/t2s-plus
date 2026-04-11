using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace UvcKsTool;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, eventArgs) => CrashReporter.Report("UI thread", eventArgs.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
                CrashReporter.Report("AppDomain", eventArgs.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception."));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        if (IsHelp(args[0]))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "devices" => RunDevices(),
                "commands" => RunCommands(),
                "send" => RunSend(args.Skip(1).ToArray()),
                _ => Fail($"Unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static bool IsHelp(string arg) =>
        arg is "-h" or "--help" or "help" or "/?";

    private static int RunDevices()
    {
        var devices = VideoDeviceFinder.ListVideoInputDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("No DirectShow video input devices found.");
            return 0;
        }

        Console.WriteLine("DirectShow video input devices:");
        foreach (var device in devices)
        {
            Console.WriteLine($"  [{device.Index}] {device.FriendlyName}");
        }

        return 0;
    }

    private static int RunCommands()
    {
        Console.WriteLine("Named commands:");
        foreach (var command in LegacyCommandCatalog.NamedCommands.OrderBy(command => command.Name))
        {
            Console.WriteLine(
                $"  {command.Name,-24} {command.Pattern,-14} {command.Summary} " +
                $"[source={command.Source}; confidence={command.ConfidenceLabel}]");
        }

        Console.WriteLine();
        Console.WriteLine("Observed raw-only patterns:");
        foreach (var pattern in LegacyCommandCatalog.RawPatterns)
        {
            Console.WriteLine(
                $"  {pattern.Name,-24} {pattern.Pattern,-18} {pattern.Summary} " +
                $"[source={pattern.Source}; confidence={pattern.ConfidenceLabel}]");
        }

        return 0;
    }

    private static int RunSend(string[] args)
    {
        var parsed = ParsedArguments.Parse(args);

        if (parsed.Device is null)
        {
            return Fail("Missing --device <name-or-index>.");
        }

        ushort value;
        string resolvedName;
        string resolvedPattern;

        if (parsed.RawValue is not null)
        {
            value = ParseUInt16(parsed.RawValue, "--raw");
            resolvedName = "raw";
            resolvedPattern = $"0x{value:X4}";
        }
        else
        {
            if (parsed.CommandName is null)
            {
                return Fail("Missing --name <command> or --raw <value>.");
            }

            var command = LegacyCommandCatalog.TryGetNamedCommand(parsed.CommandName);
            if (command is null)
            {
                return Fail($"Unknown named command: {parsed.CommandName}");
            }

            value = command.Resolve(parsed.Value);
            resolvedName = command.Name;
            resolvedPattern = command.Pattern;
        }

        var devices = VideoDeviceFinder.ListVideoInputDevices();
        var target = VideoDeviceFinder.ResolveDevice(devices, parsed.Device);
        if (target is null)
        {
            return Fail($"Could not resolve device: {parsed.Device}");
        }

        var sender = new KsCommandSender();
        var result = sender.SendAbsoluteZoomCommand(target.FriendlyName, value);
        var hr = result.HResult;

        Console.WriteLine($"device   : [{target.Index}] {target.FriendlyName}");
        Console.WriteLine($"command  : {resolvedName}");
        Console.WriteLine($"pattern  : {resolvedPattern}");
        Console.WriteLine($"resolved : 0x{value:X4}");
        Console.WriteLine("route    : PROPSETID_VIDCAP_CAMERACONTROL / ZOOM / SET");
        Console.WriteLine($"hresult  : 0x{unchecked((uint)hr):X8} ({HResultFormatter.Format(hr)})");
        if (result.BytesReturned > 0)
        {
            Console.WriteLine($"bytesRet : {result.BytesReturned}");
        }

        return hr == 0 ? 0 : 2;
    }

    private static ushort ParseUInt16(string raw, string optionName)
    {
        try
        {
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToUInt16(raw[2..], 16);
            }

            return Convert.ToUInt16(raw, 10);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"{optionName} expects a 16-bit integer value. {ex.Message}", ex);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        Console.Error.WriteLine();
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("UvcKsTool");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project UvcKsTool");
        Console.WriteLine("  dotnet run --project UvcKsTool -- devices");
        Console.WriteLine("  dotnet run --project UvcKsTool -- commands");
        Console.WriteLine("  dotnet run --project UvcKsTool -- send --device <name-or-index> --name <command> [--value <n>]");
        Console.WriteLine("  dotnet run --project UvcKsTool -- send --device <name-or-index> --raw <0xNNNN|NNNN>");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project UvcKsTool");
        Console.WriteLine("  dotnet run --project UvcKsTool -- devices");
        Console.WriteLine("  dotnet run --project UvcKsTool -- commands");
        Console.WriteLine("  dotnet run --project UvcKsTool -- send --device T2S+ --name nuc");
        Console.WriteLine("  dotnet run --project UvcKsTool -- send --device 0 --name palette --value 5");
        Console.WriteLine("  dotnet run --project UvcKsTool -- send --device T2S+ --raw 0x8081");
    }
}

internal sealed class ParsedArguments
{
    public string? Device { get; private set; }
    public string? CommandName { get; private set; }
    public string? RawValue { get; private set; }
    public int? Value { get; private set; }

    public static ParsedArguments Parse(string[] args)
    {
        var result = new ParsedArguments();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            string NextValue()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {arg}");
                }

                i++;
                return args[i];
            }

            switch (arg)
            {
                case "--device":
                    result.Device = NextValue();
                    break;
                case "--name":
                    result.CommandName = NextValue();
                    break;
                case "--raw":
                    result.RawValue = NextValue();
                    break;
                case "--value":
                    result.Value = int.Parse(NextValue());
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        return result;
    }
}

internal sealed record VideoDevice(int Index, string FriendlyName)
{
    public string DisplayText => $"[{Index}] {FriendlyName}";
}

internal static class VideoDeviceFinder
{
    private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    private static readonly Guid PropertyBagGuid = new("55272A00-42CB-11CE-8135-00AA004BB851");

    public static IReadOnlyList<VideoDevice> ListVideoInputDevices()
    {
        var list = new List<VideoDevice>();
        var devEnum = CreateDeviceEnumerator();

        var category = VideoInputDeviceCategory;
        var hr = devEnum.CreateClassEnumerator(ref category, out var enumMoniker, 0);
        if (hr != 0 || enumMoniker is null)
        {
            return list;
        }

        try
        {
            var monikers = new IMoniker[1];
            var index = 0;
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                object? bagObject = null;
                try
                {
                    var propertyBagGuid = PropertyBagGuid;
                    monikers[0].BindToStorage(null!, null!, ref propertyBagGuid, out bagObject);
                    var bag = (IPropertyBag)bagObject!;
                    object value = string.Empty;
                    var readHr = bag.Read("FriendlyName", ref value, IntPtr.Zero);
                    var name = readHr == 0 ? value?.ToString() ?? string.Empty : "<unreadable>";
                    list.Add(new VideoDevice(index++, name));
                }
                finally
                {
                    if (bagObject is not null)
                    {
                        Marshal.ReleaseComObject(bagObject);
                    }

                    if (monikers[0] is not null)
                    {
                        Marshal.ReleaseComObject(monikers[0]);
                    }
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumMoniker);
            Marshal.ReleaseComObject(devEnum);
        }

        return list;
    }

    public static VideoDevice? ResolveDevice(IReadOnlyList<VideoDevice> devices, string selector)
    {
        if (int.TryParse(selector, out var index))
        {
            return devices.FirstOrDefault(device => device.Index == index);
        }

        var exact = devices.FirstOrDefault(device =>
            string.Equals(device.FriendlyName, selector, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var partialMatches = devices
            .Where(device => device.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return partialMatches.Count == 1 ? partialMatches[0] : null;
    }

    private static ICreateDevEnum CreateDeviceEnumerator()
    {
        var type = Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"))
            ?? throw new InvalidOperationException("Could not resolve CLSID_SystemDeviceEnum.");
        return (ICreateDevEnum)Activator.CreateInstance(type)!;
    }
}

internal static class LegacyCommandCatalog
{
    public static readonly IReadOnlyList<LegacyCommand> NamedCommands =
    [
        LegacyCommand.Fixed("nuc", "0x8000", "Trigger shutter / NUC calibration.", "old demo + doc", 0.95, 0x8000),
        LegacyCommand.Fixed("source-raw", "0x8004", "Switch source to raw thermal data.", "old demo + doc", 0.95, 0x8004),
        LegacyCommand.Fixed("source-yuv", "0x8005", "Switch source to YUV display data.", "old demo", 0.90, 0x8005),
        LegacyCommand.Fixed("wide-dynamic-off", "0x8020", "Disable wide dynamic mode.", "old demo UI", 0.90, 0x8020),
        LegacyCommand.Fixed("wide-dynamic-on", "0x8021", "Enable wide dynamic mode.", "old demo UI", 0.90, 0x8021),
        LegacyCommand.Fixed("legacy-39", "0x8027", "Legacy step 1 in the old temperature-point apply sequence; not reliable as a standalone action.", "old demo", 0.20, 0x8027),
        LegacyCommand.Fixed("k-table", "0x8081", "Switch to K-value / bad-pixel calibration stream.", "new demo", 0.90, 0x8081),
        LegacyCommand.Fixed("legacy-apply", "0x80FE", "Legacy step 2 in the old apply/follow-up sequence, usually sent after 0x8027 or parameter upload.", "old demo", 0.30, 0x80FE),
        LegacyCommand.Fixed("palette-white-hot", "0x8800", "Select palette: White Hot.", "old demo UI", 0.95, 0x8800),
        LegacyCommand.Fixed("palette-black-hot", "0x8801", "Select palette: Black Hot.", "old demo UI", 0.95, 0x8801),
        LegacyCommand.Fixed("palette-blue-red-yellow", "0x8802", "Select palette: Blue / Red / Yellow.", "old demo UI", 0.95, 0x8802),
        LegacyCommand.Fixed("palette-purple-red-yellow", "0x8803", "Select palette: Purple / Red / Yellow.", "old demo UI", 0.95, 0x8803),
        LegacyCommand.Fixed("palette-blue-green-red", "0x8804", "Select palette: Blue / Green / Red.", "old demo UI", 0.95, 0x8804),
        LegacyCommand.Fixed("palette-rainbow-1", "0x8805", "Select palette: Rainbow 1.", "old demo UI", 0.95, 0x8805),
        LegacyCommand.Fixed("palette-rainbow-2", "0x8806", "Select palette: Rainbow 2.", "old demo UI", 0.95, 0x8806),
        LegacyCommand.Fixed("palette-black-red", "0x8807", "Select palette: Black / Red.", "old demo UI", 0.95, 0x8807),
        LegacyCommand.Fixed("palette-dark-green-red", "0x8808", "Select palette: Dark Green / Red.", "old demo UI", 0.95, 0x8808),
        LegacyCommand.Fixed("palette-blue-green-red-pink", "0x8809", "Select palette: Blue / Green / Red / Pink.", "old demo UI", 0.95, 0x8809),
        LegacyCommand.Fixed("palette-mixed", "0x880A", "Select palette: Mixed.", "old demo UI", 0.95, 0x880A),
        LegacyCommand.Fixed("palette-red-head", "0x880B", "Select palette: Red Head.", "old demo UI", 0.95, 0x880B),
        LegacyCommand.Template("palette", "0x8800 | index", "Palette family. Use --value 0..11.", "old demo UI", 0.95, 0x8800, 0, 11),
        LegacyCommand.Template("temp-point1-x", "0xF000 | x", "Set temperature point 1 X coordinate. Use --value 0..255.", "old demo", 0.90, 0xF000, 0, 255),
        LegacyCommand.Template("temp-point1-y", "0xF200 | y", "Set temperature point 1 Y coordinate. Use --value 0..255.", "old demo", 0.90, 0xF200, 0, 255),
        LegacyCommand.Template("temp-point2-x", "0xF400 | x", "Set temperature point 2 X coordinate. Use --value 0..255.", "old demo", 0.90, 0xF400, 0, 255),
        LegacyCommand.Template("temp-point2-y", "0xF600 | y", "Set temperature point 2 Y coordinate. Use --value 0..255.", "old demo", 0.90, 0xF600, 0, 255),
        LegacyCommand.Template("temp-point3-x", "0xF800 | x", "Set temperature point 3 X coordinate. Use --value 0..255.", "old demo", 0.90, 0xF800, 0, 255),
        LegacyCommand.Template("temp-point3-y", "0xFA00 | y", "Set temperature point 3 Y coordinate. Use --value 0..255.", "old demo", 0.90, 0xFA00, 0, 255),
        LegacyCommand.Fixed("startup-newdemo", "0xA120", "2023 demo startup init command. Behavior inferred from startup flow.", "new demo", 0.35, 0xA120)
    ];

    public static readonly IReadOnlyList<LegacyCommandPattern> RawPatterns =
    [
        new("param-byte", "(index << 8) | byte", "Upload one legacy measurement-parameter byte. Seen in SaveParam() for bytes 0..23.", "old demo", 0.75)
    ];

    public static LegacyCommand? TryGetNamedCommand(string name) =>
        NamedCommands.FirstOrDefault(command =>
            string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed record LegacyCommand(
    string Name,
    string Pattern,
    string Summary,
    string Source,
    double Confidence,
    ushort? FixedValue,
    ushort? TemplateBase,
    int MinValue,
    int MaxValue)
{
    public string ConfidenceLabel => $"{Confidence:P0}";

    public static LegacyCommand Fixed(
        string name,
        string pattern,
        string summary,
        string source,
        double confidence,
        ushort value) =>
        new(name, pattern, summary, source, confidence, value, null, 0, 0);

    public static LegacyCommand Template(
        string name,
        string pattern,
        string summary,
        string source,
        double confidence,
        ushort templateBase,
        int minValue,
        int maxValue) =>
        new(name, pattern, summary, source, confidence, null, templateBase, minValue, maxValue);

    public ushort Resolve(int? value)
    {
        if (FixedValue is not null)
        {
            return FixedValue.Value;
        }

        if (TemplateBase is null)
        {
            throw new InvalidOperationException($"Command {Name} is not sendable.");
        }

        if (value is null)
        {
            throw new ArgumentException($"Command {Name} requires --value {MinValue}..{MaxValue}.");
        }

        if (value < MinValue || value > MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Command {Name} expects {MinValue}..{MaxValue}.");
        }

        return (ushort)(TemplateBase.Value | value.Value);
    }
}

internal sealed record LegacyCommandPattern(
    string Name,
    string Pattern,
    string Summary,
    string Source,
    double Confidence)
{
    public string ConfidenceLabel => $"{Confidence:P0}";
}

internal sealed class KsCommandSender
{
    private const int ZoomPropertyId = 3;
    private const int KsPropertyTypeSet = 0x00000002;
    private const int CameraControlFlagManual = 0x0002;
    private static readonly int InsufficientBufferHr = unchecked((int)0x8007007A);
    private static readonly int[] RetryBufferSizes = [64, 128, 256];
    private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    private static readonly Guid PropertyBagGuid = new("55272A00-42CB-11CE-8135-00AA004BB851");
    private static readonly Guid BaseFilterGuid = new("56A86895-0AD4-11CE-B03A-0020AF0BA770");
    private static readonly Guid KsControlGuid = new("28F54685-06FD-11D2-B27A-00A0C9223196");
    private static readonly Guid CameraControlPropertySet = new("C6E13370-30AC-11D0-A18C-00A0C9118956");

    public KsSendResult SendAbsoluteZoomCommand(string targetFriendlyName, ushort command)
    {
        var devEnum = CreateDeviceEnumerator();
        var category = VideoInputDeviceCategory;
        var hr = devEnum.CreateClassEnumerator(ref category, out var enumMoniker, 0);
        if (hr != 0 || enumMoniker is null)
        {
            return new KsSendResult(hr, 0);
        }

        try
        {
            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                object? bagObject = null;
                object? filterObject = null;
                IntPtr unknown = IntPtr.Zero;
                IntPtr ksControlPointer = IntPtr.Zero;

                try
                {
                    var propertyBagGuid = PropertyBagGuid;
                    monikers[0].BindToStorage(null!, null!, ref propertyBagGuid, out bagObject);
                    var bag = (IPropertyBag)bagObject!;
                    object value = string.Empty;
                    var readHr = bag.Read("FriendlyName", ref value, IntPtr.Zero);
                    var friendlyName = readHr == 0 ? value?.ToString() ?? string.Empty : string.Empty;
                    if (!string.Equals(friendlyName, targetFriendlyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var baseFilterGuid = BaseFilterGuid;
                    monikers[0].BindToObject(null!, null!, ref baseFilterGuid, out filterObject);
                    unknown = Marshal.GetIUnknownForObject(filterObject!);

                    var ksControlGuid = KsControlGuid;
                    var queryHr = Marshal.QueryInterface(unknown, in ksControlGuid, out ksControlPointer);
                    if (queryHr != 0)
                    {
                        return new KsSendResult(queryHr, 0);
                    }

                    var ksControl = (IKsControl)Marshal.GetObjectForIUnknown(ksControlPointer);
                    return SendZoomKsProperty(ksControl, command);
                }
                finally
                {
                    if (ksControlPointer != IntPtr.Zero)
                    {
                        Marshal.Release(ksControlPointer);
                    }

                    if (unknown != IntPtr.Zero)
                    {
                        Marshal.Release(unknown);
                    }

                    if (filterObject is not null)
                    {
                        Marshal.ReleaseComObject(filterObject);
                    }

                    if (bagObject is not null)
                    {
                        Marshal.ReleaseComObject(bagObject);
                    }

                    if (monikers[0] is not null)
                    {
                        Marshal.ReleaseComObject(monikers[0]);
                    }
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumMoniker);
            Marshal.ReleaseComObject(devEnum);
        }

        return new KsSendResult(unchecked((int)0x80004005), 0);
    }

    private static ICreateDevEnum CreateDeviceEnumerator()
    {
        var type = Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"))
            ?? throw new InvalidOperationException("Could not resolve CLSID_SystemDeviceEnum.");
        return (ICreateDevEnum)Activator.CreateInstance(type)!;
    }

    private KsSendResult SendZoomKsProperty(IKsControl control, ushort command)
    {
        var payload = new KsCameraControlProperty
        {
            Property = new KsProperty
            {
                Set = CameraControlPropertySet,
                Id = ZoomPropertyId,
                Flags = KsPropertyTypeSet
            },
            Value = command,
            Flags = CameraControlFlagManual,
            Capabilities = CameraControlFlagManual
        };

        var propertyBytes = StructureToBytes(payload.Property);
        var payloadBytes = StructureToBytes(payload);

        var result = InvokeKsProperty(control, payloadBytes, payloadBytes.Length, payloadBytes, payloadBytes.Length);
        if (result.HResult == 0)
        {
            return result;
        }

        if (result.HResult != InsufficientBufferHr)
        {
            return result;
        }

        result = InvokeKsProperty(control, propertyBytes, propertyBytes.Length, payloadBytes, payloadBytes.Length);
        if (result.HResult == 0 || result.HResult != InsufficientBufferHr)
        {
            return result;
        }

        foreach (var retrySize in GetRetryBufferSizes(payloadBytes.Length, result.BytesReturned))
        {
            var paddedPayload = PadBuffer(payloadBytes, retrySize);

            result = InvokeKsProperty(control, propertyBytes, propertyBytes.Length, paddedPayload, paddedPayload.Length);
            if (result.HResult == 0 || result.HResult != InsufficientBufferHr)
            {
                return result;
            }

            result = InvokeKsProperty(control, paddedPayload, paddedPayload.Length, paddedPayload, paddedPayload.Length);
            if (result.HResult == 0 || result.HResult != InsufficientBufferHr)
            {
                return result;
            }
        }

        return result;
    }

    private static KsSendResult InvokeKsProperty(
        IKsControl control,
        byte[] propertyBuffer,
        int propertyLength,
        byte[] dataBuffer,
        int dataLength)
    {
        IntPtr propertyPointer = IntPtr.Zero;
        IntPtr dataPointer = IntPtr.Zero;

        try
        {
            propertyPointer = Marshal.AllocHGlobal(propertyLength);
            Marshal.Copy(propertyBuffer, 0, propertyPointer, propertyLength);

            dataPointer = Marshal.AllocHGlobal(dataLength);
            Marshal.Copy(dataBuffer, 0, dataPointer, dataLength);

            var hr = control.KsProperty(propertyPointer, propertyLength, dataPointer, dataLength, out var bytesReturned);
            return new KsSendResult(hr, bytesReturned);
        }
        finally
        {
            if (dataPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(dataPointer);
            }

            if (propertyPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(propertyPointer);
            }
        }
    }

    private static byte[] StructureToBytes<T>(T value)
        where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var pointer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            var buffer = new byte[size];
            Marshal.Copy(pointer, buffer, 0, size);
            return buffer;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static byte[] PadBuffer(byte[] source, int size)
    {
        if (size <= source.Length)
        {
            return source;
        }

        var buffer = new byte[size];
        Buffer.BlockCopy(source, 0, buffer, 0, source.Length);
        return buffer;
    }

    private static IEnumerable<int> GetRetryBufferSizes(int minimumSize, int bytesReturned)
    {
        if (bytesReturned > minimumSize)
        {
            yield return bytesReturned;
        }

        foreach (var retrySize in RetryBufferSizes)
        {
            if (retrySize > minimumSize && retrySize != bytesReturned)
            {
                yield return retrySize;
            }
        }
    }
}

internal readonly record struct KsSendResult(int HResult, int BytesReturned);

internal static class HResultFormatter
{
    public static string Format(int hr)
    {
        if (hr == 0)
        {
            return "S_OK";
        }

        return hr switch
        {
            unchecked((int)0x8007007A) => "HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)",
            unchecked((int)0x800700EA) => "HRESULT_FROM_WIN32(ERROR_MORE_DATA)",
            unchecked((int)0x80004005) => "E_FAIL",
            unchecked((int)0x80070057) => "HRESULT_FROM_WIN32(ERROR_INVALID_PARAMETER)",
            unchecked((int)0x80070490) => "HRESULT_FROM_WIN32(ERROR_NOT_FOUND)",
            unchecked((int)0x80040154) => "REGDB_E_CLASSNOTREG",
            _ => new COMException("Native call failed.", hr).Message
        };
    }
}

internal static class CrashReporter
{
    private static readonly object Sync = new();

    public static void Report(string source, Exception exception)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var logPath = Path.Combine(baseDir, "crash.log");
            var payload =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";

            lock (Sync)
            {
                File.AppendAllText(logPath, payload);
            }

            MessageBox.Show(
                $"Unhandled exception in {source}.{Environment.NewLine}{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}Details saved to:{Environment.NewLine}{logPath}",
                "UvcKsTool Crash",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
        }
    }
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
internal interface ICreateDevEnum
{
    [PreserveSig]
    int CreateClassEnumerator(ref Guid type, out IEnumMoniker? enumMoniker, int flags);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
internal interface IPropertyBag
{
    [PreserveSig]
    int Read([MarshalAs(UnmanagedType.LPWStr)] string propertyName, [In, Out, MarshalAs(UnmanagedType.Struct)] ref object value, IntPtr errorLog);

    [PreserveSig]
    int Write([MarshalAs(UnmanagedType.LPWStr)] string propertyName, [MarshalAs(UnmanagedType.Struct)] ref object value);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
internal interface IKsControl
{
    [PreserveSig]
    int KsProperty(IntPtr property, int propertyLength, IntPtr propertyData, int dataLength, out int bytesReturned);

    [PreserveSig]
    int KsMethod(IntPtr method, int methodLength, IntPtr methodData, int dataLength, out int bytesReturned);

    [PreserveSig]
    int KsEvent(IntPtr eventData, int eventLength, IntPtr eventPayload, int dataLength, out int bytesReturned);
}

[StructLayout(LayoutKind.Sequential)]
internal struct KsProperty
{
    public Guid Set;
    public int Id;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KsCameraControlProperty
{
    public KsProperty Property;
    public int Value;
    public uint Flags;
    public uint Capabilities;
}
