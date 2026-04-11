# Xinfrared T2S Plus Tools

Windows tooling for the **Xinfrared T2S Plus** thermal camera.

This repository currently contains **UvcKsTool**, a WinForms utility that combines:

- live preview through OpenCV + DirectShow
- device enumeration through DirectShow
- low-level `IKsControl` command sending over `PROPSETID_VIDCAP_CAMERACONTROL / ZOOM / SET`
- a small catalog of legacy commands observed in older demo software

This is a reverse-engineering and testing utility, not an official Xinfrared release.

## Current Status

The tool is useful for exploring command behavior on Windows and for reproducing parts of the older demo workflow on the T2S Plus.

It now supports:

- a desktop UI with device selection, preview, logs, palette controls, temperature point editors, and raw command entry
- CLI commands for listing devices, listing known commands, and sending one command at a time
- a legacy temperature-point commit flow that sends `0x8027 -> 0x80FE` as a sequence instead of exposing `0x8027` as a standalone UI action
- extra KS logging, including returned byte counts when the driver reports a buffer-size issue

## Requirements

- Windows 10 or Windows 11
- .NET 9 SDK
- a UVC-compatible camera path visible to DirectShow
- tested primarily against **Xinfrared T2S Plus**

## Build

```powershell
dotnet build .\UvcKsTool\UvcKsTool.csproj
```

## Run The UI

```powershell
dotnet run --project .\UvcKsTool
```

What the UI does:

- `Refresh` lists DirectShow video devices
- `Start Preview` opens the selected camera through OpenCV/DirectShow
- command buttons send KS requests to the selected device
- `Commit Temp Points` runs the observed follow-up sequence for temperature-point updates
- the log pane prints the resolved command value, HRESULT, and any returned byte count

## CLI Usage

List devices:

```powershell
dotnet run --project .\UvcKsTool -- devices
```

List built-in commands:

```powershell
dotnet run --project .\UvcKsTool -- commands
```

Send a named command:

```powershell
dotnet run --project .\UvcKsTool -- send --device 0 --name nuc
```

Send a templated command:

```powershell
dotnet run --project .\UvcKsTool -- send --device 0 --name palette --value 5
```

Send a raw 16-bit value:

```powershell
dotnet run --project .\UvcKsTool -- send --device 0 --raw 0x8081
```

## Known Command Coverage

Higher-confidence commands in the catalog include:

- `0x8000` for shutter / NUC
- `0x8004` and `0x8005` for source switching
- `0x8020` and `0x8021` for wide dynamic mode
- `0x8081` for K-table / calibration stream switching
- `0x8800..0x880B` for palettes
- `0xF000..0xFA00` templates for temperature-point X/Y coordinates

Lower-confidence items are still documented, but should be treated carefully:

- `0xA120` startup-related command from a newer demo path
- `(index << 8) | byte` parameter-upload pattern from `SaveParam()`
- `0x80FE` legacy apply/follow-up step
- `0x8027` legacy step observed before `0x80FE`

## Notes And Caveats

- A successful preview path does not guarantee that a KS command will be accepted.
- A successful KS command does not guarantee that preview is using the same internal camera mode.
- Nonzero `HRESULT` values usually mean the driver rejected the request, the current camera state is incompatible, or the buffer layout was not accepted.
- Some legacy commands were inferred from old demo software and may be device-, firmware-, or mode-specific.
- This tool does not implement temperature decoding, radiometric calibration, or a production imaging pipeline.

## Repository Layout

- [UvcKsTool/](UvcKsTool/) - WinForms app and CLI entrypoint
- [UvcKsTool/Program.cs](UvcKsTool/Program.cs) - CLI, command catalog, KS sender
- [UvcKsTool/MainForm.cs](UvcKsTool/MainForm.cs) - desktop UI
- [UvcKsTool/CameraPreviewController.cs](UvcKsTool/CameraPreviewController.cs) - preview pipeline

## License

This repository is licensed under the [MIT License](LICENSE).
