# WY直链下载器

一个功能强大、界面现代化的 Windows 桌面直链下载器，支持 HTTP/HTTPS 协议的文件下载和 ZIP 文件自动解压功能。

## ✨ 功能特性

### 核心功能
- 🚀 **直链下载**：支持 HTTP/HTTPS 协议的文件下载
- 📦 **自动解压**：下载完成后可自动解压 ZIP 文件
- 📊 **实时进度**：显示下载进度、速度和文件大小
- ⏸️ **暂停/继续**：支持暂停和继续下载操作
- 🔄 **断点续传**：支持断点续传功能，避免重复下载

### 界面特性
- 🎨 **现代化界面**：采用 Material Design 设计风格
- 🖼️ **自定义背景**：支持配置多个背景图片随机切换
- 🎯 **简洁易用**：直观的操作界面，一键下载

### 配置特性
- ⚙️ **灵活配置**：通过 `config.ini` 文件配置下载链接
- 🌐 **镜像支持**：支持配置多个备用下载地址
- 📁 **自定义路径**：可配置默认下载保存路径

## 📋 系统要求

- Windows 7 SP1 或更高版本
- .NET Framework 4.7.2 或更高版本
- 至少 50MB 可用磁盘空间

## 🚀 快速开始

### 1. 启动程序

双击 `WYDownloader.exe` 启动程序

### 2. 选择下载项目

- 在下拉框中选择要下载的项目
- 程序会自动加载配置的下载链接

### 3. 开始下载

- 点击"开始下载"按钮开始下载
- 实时查看下载进度、速度和状态信息
- 可随时暂停或继续下载

### 4. 自动解压

- 勾选"自动解压 ZIP"选项
- ZIP 文件下载完成后会自动解压

## ⚙️ 配置说明

配置文件位于 `Resources/Config/config.ini`，支持以下配置：

### [Settings] - 基本设置

```ini
[Settings]
; 是否自动解压 ZIP 文件 (true/false)
AutoExtractZip=true

; 是否启用断点续传 (true/false)
EnableResume=true

; 默认下载路径（为空则使用程序所在目录）
DefaultDownloadPath=

; 背景图片列表（多个用 | 分隔，为空则使用默认背景）
; 示例：bg1.jpg|bg2.jpg|bg3.jpg
BackgroundImages=

; 默认选择的下载项目（对应 Downloads 节中的键名）
DefaultDownload=Keira工具
```

### [Servers] - 服务器配置

```ini
[Servers]
; 下载资源服务器地址（用于相对路径，可选）
BaseUrl=

; 备用镜像地址（可选，多个用 | 分隔）
Mirrors=
```

### [Downloads] - 下载链接配置

```ini
[Downloads]
; 下载链接配置格式：名称=URL
; 可以添加多个下载链接，每行一个
项目名称=https://example.com/file.zip
另一个项目=https://example.com/file2.zip
```

### [Announcement] - 公告配置

```ini
[Announcement]
; 公告标题
Title=WY直链下载器

; 公告内容 (支持多行，用 | 分隔)
Content=欢迎使用WY直链下载器！|• 支持HTTP/HTTPS协议的直链下载|• 实时显示下载进度、速度和文件大小
```

### [UI] - 界面配置

```ini
[UI]
; 窗口宽度（像素）
WindowWidth=1000

; 窗口高度（像素）
WindowHeight=700
```

## 🖼️ 自定义背景图片

### 使用自定义背景

1. 将背景图片放入 `Resources/Images/background/` 目录
2. 在 `config.ini` 的 `[Settings]` 节中配置 `BackgroundImages`
3. 多个背景图片用 `|` 分隔，程序启动时会随机选择一张

### 示例配置

```ini
[Settings]
BackgroundImages=bg1.jpg|bg2.jpg|bg3.jpg
```

### 支持的图片格式

- JPG / JPEG
- PNG
- BMP
- 其他常见图片格式

## 📂 项目结构

```
WYDownloader/
├── App.xaml / App.xaml.cs      # 应用程序入口
├── MainWindow.xaml             # UI 布局
├── MainWindow.xaml.cs          # UI 逻辑
├── Core/                       # 核心功能层
│   ├── ConfigManager.cs        # 配置管理
│   └── DownloadManager.cs      # 下载管理
├── Resources/                  # 资源文件夹
│   ├── Images/
│   │   ├── BG.JPG              # 默认背景图片
│   │   └── background/         # 自定义背景图片目录
│   ├── Config/
│   │   └── config.ini          # 配置文件
│   └── Icons/
│       └── ico.ico             # 应用图标
└── Properties/                 # 程序集信息和资源
```

## 🔧 开发环境

### 技术栈

- **框架**：WPF (.NET Framework 4.7.2)
- **UI 库**：MaterialDesignInXaml
- **语言**：C# 8.0
- **IDE**：Visual Studio 2019/2022

### 构建项目

```bash
# 使用 MSBuild 构建
msbuild WYDownloader.sln /p:Configuration=Release

# 或在 Visual Studio 中打开 WYDownloader.sln 并构建
```

### 依赖包

- MaterialDesignThemes (4.9.0)
- MaterialDesignColors (2.1.4)

## 📝 使用说明

### 添加新的下载项目

1. 打开 `Resources/Config/config.ini`
2. 在 `[Downloads]` 节中添加新行
3. 格式：`项目名称=下载链接`
4. 保存文件并重启程序

### 使用镜像地址

如果下载链接使用相对路径，可以配置服务器地址：

```ini
[Servers]
BaseUrl=https://primary-server.com
Mirrors=https://mirror1.com|https://mirror2.com

[Downloads]
项目名称=/downloads/file.zip
```

程序会自动尝试所有镜像地址直到下载成功。

### 修改默认下载路径

```ini
[Settings]
DefaultDownloadPath=D:\Downloads
```

留空则使用程序所在目录。

## ⚠️ 注意事项

- 确保网络连接正常
- 确保有足够的磁盘空间存储下载文件
- 某些网站可能有防盗链保护，可能无法直接下载
- 下载大文件时请保持程序运行，避免中途关闭
- ZIP 解压功能需要文件完整下载后才能执行
- 取消下载会保留 `.part` 文件，便于下次续传

## 🐛 故障排除

### 下载失败

1. 检查网络连接
2. 确认下载链接是否有效
3. 尝试使用镜像地址
4. 查看错误提示信息

### 配置文件加载失败

1. 确认 `config.ini` 文件格式正确
2. 检查文件编码是否为 UTF-8
3. 查看程序日志获取详细错误信息

### 背景图片不显示

1. 确认图片文件存在
2. 检查配置中的图片路径
3. 确认图片格式受支持

## 📄 版本信息

- **版本**：2.0.0
- **发布日期**：2025年
- **开发者**：WY

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

## 📜 许可证

本项目采用 MIT 许可证。

---

如有问题或建议，欢迎反馈！