# WYDownloader

Windows 直链下载器，支持断点续传与 ZIP 自动解压。

## 功能

- HTTP/HTTPS 直链下载
- 断点续传（保留 `.part` 文件）
- ZIP 自动解压
- 多镜像回退
- 配置驱动

## 配置

编辑 `Resources/Config/config.ini`：

```ini
[Settings]
AutoExtractZip=true
EnableResume=true
DefaultDownload=Keira工具

[Downloads]
Keira工具=https://example.com/file.zip

[Announcement]
Title=标题
Content=内容|• 功能1|• 功能2
```

## 构建

```bash
msbuild WYDownloader.sln /p:Configuration=Release
```

## 运行要求

- Windows 7 SP1+
- .NET Framework 4.7.2