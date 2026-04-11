# UvcKsTool

一个最小的 Windows 命令行工具，用 `IKsControl` 通过 `PROPSETID_VIDCAP_CAMERACONTROL / ZOOM` 通道，向 UVC 热像仪发送旧版 demo 里出现过的私有命令。

## 功能

- 列出 DirectShow 视频输入设备
- 列出内置旧版命令表
- 按命令名或原始十六进制值发命令

## 运行

```powershell
dotnet run --project .\UvcKsTool -- devices
dotnet run --project .\UvcKsTool -- commands
dotnet run --project .\UvcKsTool -- send --device T2S+ --name nuc
dotnet run --project .\UvcKsTool -- send --device T2S+ --name palette --value 5
dotnet run --project .\UvcKsTool -- send --device T2S+ --raw 0x8081
```

## 已整理的旧版命令

高置信度：

- `0x8000`：快门 / NUC 校正
- `0x8004`：切原始数据
- `0x8005`：切 YUV 数据
- `0x8020`：关闭宽动态
- `0x8021`：打开宽动态
- `0x8081`：切到 K 值 / 坏点校正数据流
- `0x8800..0x880B`：调色板切换
- `0xF000/0xF200/0xF400/0xF600/0xF800/0xFA00`：三个测温点的 X/Y 坐标

中低置信度：

- `0xA120`：2023 版 demo 启动时发送的一次初始化命令
- `(index << 8) | byte`：旧版 `SaveParam()` 路径里逐字节上传测温参数
- `0x80FE`：旧版参数更新 / 原始保存路径里出现的后续命令，确切用途还不清楚
- `0x8027`：旧版温度设置路径里出现，紧接着会发 `0x80FE`

## 说明

- 这个工具只负责“发命令”，不做采集、解码、测温或伪彩。
- 命令名优先走内置命令表；其余值可以用 `--raw` 直接发。
- 如果返回的 `HRESULT` 不是 `0x00000000`，说明驱动层没有接受该请求，或当前设备状态不满足这条命令的使用条件。
