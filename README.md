# WYDownloader

简洁的 Windows 直链下载器（WPF / .NET Framework 4.7.2），支持下载进度、断点续传、ZIP 自动解压和配置化链接管理。

## 功能

- 直链下载：支持 HTTP/HTTPS
- 断点续传：取消后可继续下载（保留 `.part`）
- 自动解压：下载 ZIP 后可自动解压
- 镜像回退：支持主地址 + 备用镜像
- 配置驱动：下载项、公告、窗口尺寸都可通过 `config.ini` 调整

## 运行环境

- Windows 7 SP1+
- .NET Framework 4.7.2
- Visual Studio 2019/2022（开发）

## 快速使用

1. 运行程序（`bin\Release\WYDownloader.exe` 或 `bin\Debug\WYDownloader.exe`）
2. 在“选择下载项目”下拉框选择项目
3. 点击“开始下载”
4. 如需自动解压，保持“自动解压 ZIP”勾选

## 配置文件

配置路径：`Resources/Config/config.ini`

示例：

```ini
[Settings]
AutoExtractZip=true
EnableResume=true
DefaultDownloadPath=
BackgroundImages=
DefaultDownload=Keira工具

[Servers]
BaseUrl=
Mirrors=

[Downloads]
Keira工具=https://example.com/file.zip

[Announcement]
Title=直链下载器
Content=欢迎使用|• 支持断点续传|• 支持自动解压

[UI]
WindowWidth=900
WindowHeight=505
```

说明：

- `DefaultDownloadPath` 为空时，默认保存到程序目录
- `Mirrors` 多个地址用 `|` 分隔
- `Content` 多行用 `|` 分隔

## 构建

```bash
msbuild WYDownloader.sln /p:Configuration=Debug
msbuild WYDownloader.sln /p:Configuration=Release
```

## 项目结构

```text
WYDownloader/
├─ App.xaml
├─ MainWindow.xaml
├─ MainWindow.xaml.cs
├─ Core/
│  ├─ ConfigManager.cs
│  ├─ DownloadManager.cs
│  └─ Logger.cs
├─ Resources/
│  ├─ Config/config.ini
│  ├─ Icons/ico.ico
│  └─ Images/
├─ NLog.config
├─ WYDownloader.csproj
└─ WYDownloader.sln
```

## 日志

- 默认输出目录：程序运行目录下的 `logs/`
- 日志配置文件：`NLog.config`
