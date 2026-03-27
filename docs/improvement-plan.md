# WYDownloader 改进实施方案

## 概述

本文档提供从当前架构到目标架构的详细实施路径。

---

## 阶段一：安全与稳定性修复（P0 - 1周内完成）

### 1.1 修复线程安全问题

#### 问题描述
`DownloadManager` 中的 `IsPaused` 标志在多线程环境下不安全。

#### 解决方案

创建 `ThreadSafeBoolean.cs`：

```csharp
using System;
using System.Threading;

namespace WYDownloader.Core
{
    /// <summary>
    /// 线程安全的布尔值包装器
    /// </summary>
    public class ThreadSafeBoolean
    {
        private int _value;

        public bool Value
        {
            get => Interlocked.CompareExchange(ref _value, 0, 0) != 0;
            set => Interlocked.Exchange(ref _value, value ? 1 : 0);
        }

        public ThreadSafeBoolean(bool initialValue = false)
        {
            _value = initialValue ? 1 : 0;
        }
    }
}
```

修改 `DownloadManager.cs`：

```csharp
public class DownloadManager : IDisposable
{
    // 替换原有 bool 字段
    private readonly ThreadSafeBoolean _isPaused = new ThreadSafeBoolean();

    public bool IsPaused
    {
        get => _isPaused.Value;
        private set => _isPaused.Value = value;
    }

    // 使用 SemaphoreSlim 替代忙等待
    private readonly SemaphoreSlim _pauseSemaphore = new SemaphoreSlim(1, 1);

    public async Task PauseAsync()
    {
        if (!IsDownloading) return;

        IsPaused = true;
        await _pauseSemaphore.WaitAsync();
        downloadStopwatch.Stop();
    }

    public void Resume()
    {
        if (!IsDownloading) return;

        IsPaused = false;
        _pauseSemaphore.Release();
        downloadStopwatch.Start();
    }

    private async Task DownloadFileInternalAsync(string url, string filePath,
        long existingBytes, CancellationToken cancellationToken)
    {
        // ... 原有代码 ...

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 替换忙等待为信号等待
            if (IsPaused)
            {
                await _pauseSemaphore.WaitAsync(cancellationToken);
                _pauseSemaphore.Release();
            }

            // ... 原有代码 ...
        } while (isMoreToRead);
    }
}
```

### 1.2 修复资源泄漏

#### 问题描述
`HttpResponseMessage` 在异常情况下可能未释放。

#### 解决方案

修改 `DownloadManager.cs` 中的 `DownloadFileInternalAsync`：

```csharp
private async Task DownloadFileInternalAsync(string url, string filePath,
    long existingBytes, CancellationToken cancellationToken)
{
    long resumeBytes = existingBytes;
    bool triedRange = resumeBytes > 0 && enableResume;

    // 使用 using 声明确保释放
    using var response = await GetResponseAsync(url, resumeBytes, cancellationToken);

    // 处理响应...
    response.EnsureSuccessStatusCode();

    // ... 其余代码
}

private async Task<HttpResponseMessage> GetResponseAsync(string url, long resumeBytes,
    CancellationToken cancellationToken)
{
    while (true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (enableResume && resumeBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeBytes, null);
        }

        var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        // 验证和处理响应...
        if (NeedsRetry(response, resumeBytes))
        {
            response.Dispose();
            resumeBytes = 0;
            continue;
        }

        return response;
    }
}
```

### 1.3 强化 ZIP 路径验证

创建 `ZipSecurityValidator.cs`：

```csharp
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace WYDownloader.Core.Security
{
    /// <summary>
    /// ZIP 文件安全验证器
    /// </summary>
    public static class ZipSecurityValidator
    {
        /// <summary>
        /// 危险的文件扩展名列表
        /// </summary>
        private static readonly string[] DangerousExtensions =
        {
            ".exe", ".dll", ".bat", ".cmd", ".sh", ".ps1",
            ".vbs", ".js", ".wsf", ".hta", ".scr"
        };

        /// <summary>
        /// 验证并获取安全的解压路径
        /// </summary>
        public static bool TryGetSafeExtractPath(
            ZipArchiveEntry entry,
            string extractRoot,
            out string safePath)
        {
            safePath = null;

            if (string.IsNullOrEmpty(entry.Name))
                return false;

            // 获取完整目标路径
            string destinationPath = Path.GetFullPath(
                Path.Combine(extractRoot, entry.FullName));

            // 验证路径在目标目录内
            string fullExtractRoot = Path.GetFullPath(extractRoot) + Path.DirectorySeparatorChar;
            if (!destinationPath.StartsWith(fullExtractRoot, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn($"阻止潜在的路径遍历攻击: {entry.FullName}");
                return false;
            }

            // 检查文件扩展名
            string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (DangerousExtensions.Contains(extension))
            {
                Logger.Warn($"阻止危险文件类型: {entry.Name}");
                return false;
            }

            // 检查文件大小（防止压缩炸弹）
            const long MAX_FILE_SIZE = 100 * 1024 * 1024; // 100MB
            if (entry.Length > MAX_FILE_SIZE)
            {
                Logger.Warn($"文件过大: {entry.Name} ({entry.Length} bytes)");
                return false;
            }

            safePath = destinationPath;
            return true;
        }

        /// <summary>
        /// 计算解压后的总大小
        /// </summary>
        public static long CalculateTotalSize(ZipArchive archive)
        {
            return archive.Entries.Sum(e => e.Length);
        }

        /// <summary>
        /// 检查是否为压缩炸弹
        /// </summary>
        public static bool IsZipBomb(ZipArchive archive, double thresholdRatio = 10.0)
        {
            long compressedSize = archive.Entries.Sum(e => e.CompressedLength);
            long uncompressedSize = CalculateTotalSize(archive);

            if (compressedSize == 0) return false;

            double ratio = (double)uncompressedSize / compressedSize;
            return ratio > thresholdRatio;
        }
    }
}
```

---

## 阶段二：架构重构（P1 - 1-2月完成）

### 2.1 服务接口定义

创建 `Contracts` 目录和接口定义：

```csharp
// Contracts/IDownloadService.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace WYDownloader.Contracts
{
    /// <summary>
    /// 下载服务接口
    /// </summary>
    public interface IDownloadService : IDisposable
    {
        bool IsDownloading { get; }
        bool IsPaused { get; }

        event EventHandler<DownloadProgressInfo> ProgressChanged;
        event EventHandler<DownloadCompletedInfo> DownloadCompleted;
        event EventHandler<DownloadErrorInfo> DownloadError;

        Task<DownloadResult> DownloadAsync(
            DownloadRequest request,
            CancellationToken cancellationToken = default);

        void Pause();
        void Resume();
        void Cancel();
    }
}
```

```csharp
// Contracts/DownloadModels.cs
using System;
using System.Collections.Generic;

namespace WYDownloader.Contracts
{
    /// <summary>
    /// 下载请求
    /// </summary>
    public class DownloadRequest
    {
        public string Url { get; set; }
        public string SavePath { get; set; }
        public string FileName { get; set; }
        public bool EnableResume { get; set; } = true;
        public Dictionary<string, string> Headers { get; set; }
    }

    /// <summary>
    /// 下载结果
    /// </summary>
    public class DownloadResult
    {
        public bool Success { get; set; }
        public string FilePath { get; set; }
        public bool Cancelled { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// 下载进度信息
    /// </summary>
    public class DownloadProgressInfo : EventArgs
    {
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public double ProgressPercentage => TotalBytes > 0
            ? (double)BytesReceived / TotalBytes * 100
            : 0;
        public long Speed { get; set; }
        public TimeSpan? EstimatedTimeRemaining { get; set; }
    }
}
```

### 2.2 ViewModel 层实现

使用 `CommunityToolkit.Mvvm` 包：

```csharp
// ViewModels/MainViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using WYDownloader.Contracts;

namespace WYDownloader.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IDownloadService _downloadService;
        private readonly IConfigurationService _configService;

        [ObservableProperty]
        private ObservableCollection<DownloadItemViewModel> _downloadItems;

        [ObservableProperty]
        private DownloadItemViewModel _selectedItem;

        [ObservableProperty]
        private double _currentProgress;

        [ObservableProperty]
        private string _statusMessage;

        [ObservableProperty]
        private bool _isDownloading;

        [ObservableProperty]
        private bool _isPaused;

        public MainViewModel(IDownloadService downloadService, IConfigurationService configService)
        {
            _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));

            _downloadService.ProgressChanged += OnDownloadProgressChanged;
            _downloadService.DownloadCompleted += OnDownloadCompleted;

            LoadConfiguration();
        }

        [RelayCommand(CanExecute = nameof(CanStartDownload))]
        private async Task StartDownloadAsync()
        {
            if (SelectedItem == null) return;

            IsDownloading = true;
            StatusMessage = "正在连接...";

            try
            {
                var request = new DownloadRequest
                {
                    Url = SelectedItem.Url,
                    SavePath = _configService.GetDefaultDownloadPath(),
                    EnableResume = _configService.GetEnableResume()
                };

                var result = await _downloadService.DownloadAsync(request);

                if (result.Success)
                {
                    StatusMessage = "下载完成";

                    if (SelectedItem.AutoExtract && result.FilePath.EndsWith(".zip"))
                    {
                        await ExtractAsync(result.FilePath);
                    }
                }
                else if (!result.Cancelled)
                {
                    StatusMessage = $"下载失败: {result.ErrorMessage}";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"错误: {ex.Message}";
            }
            finally
            {
                IsDownloading = false;
                IsPaused = false;
            }
        }

        [RelayCommand]
        private void CancelDownload()
        {
            _downloadService.Cancel();
            StatusMessage = "正在取消...";
        }

        [RelayCommand]
        private void TogglePause()
        {
            if (IsPaused)
            {
                _downloadService.Resume();
                IsPaused = false;
                StatusMessage = "继续下载...";
            }
            else
            {
                _downloadService.Pause();
                IsPaused = true;
                StatusMessage = "已暂停";
            }
        }

        private bool CanStartDownload => !IsDownloading && SelectedItem != null;

        partial void OnIsDownloadingChanged(bool value)
        {
            StartDownloadCommand.NotifyCanExecuteChanged();
        }

        partial void OnSelectedItemChanged(DownloadItemViewModel value)
        {
            StartDownloadCommand.NotifyCanExecuteChanged();
        }

        private void OnDownloadProgressChanged(object sender, DownloadProgressInfo e)
        {
            CurrentProgress = e.ProgressPercentage;
            StatusMessage = $"{FormatBytes(e.BytesReceived)} / {FormatBytes(e.TotalBytes)} ({e.ProgressPercentage:F1}%)";
        }

        private void OnDownloadCompleted(object sender, DownloadCompletedInfo e)
        {
            // 处理完成事件
        }

        private static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number = number / 1024;
                counter++;
            }
            return $"{number:n1} {suffixes[counter]}";
        }
    }
}
```

### 2.3 依赖注入配置

创建 `ServiceCollectionExtensions.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using WYDownloader.Contracts;
using WYDownloader.Services;
using WYDownloader.ViewModels;

namespace WYDownloader
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddWYDownloaderServices(this IServiceCollection services)
        {
            // 核心服务（单例）
            services.AddSingleton<IConfigurationService, ConfigurationService>();
            services.AddSingleton<ILoggerService, LoggerService>();

            // 下载服务（每个下载一个实例）
            services.AddTransient<IDownloadService, DownloadService>();
            services.AddTransient<IZipService, ZipService>();

            // ViewModels
            services.AddTransient<MainViewModel>();
            services.AddTransient<SettingsViewModel>();

            // HTTP Client
            services.AddHttpClient("DownloadClient", client =>
            {
                client.Timeout = TimeSpan.FromMinutes(30);
                client.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            });

            return services;
        }
    }
}
```

修改 `App.xaml.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using WYDownloader.ViewModels;

namespace WYDownloader
{
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            services.AddWYDownloaderServices();

            ServiceProvider = services.BuildServiceProvider();

            var mainWindow = new MainWindow
            {
                DataContext = ServiceProvider.GetRequiredService<MainViewModel>()
            };
            mainWindow.Show();
        }
    }
}
```

---

## 阶段三：现代化迁移（P2 - 3-6月完成）

### 3.1 .NET 8 迁移步骤

1. **创建新项目文件** `WYDownloader.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <ApplicationIcon>Resources\Icons\ico.ico</ApplicationIcon>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
    <PackageReference Include="MaterialDesignThemes" Version="4.9.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="8.0.0" />
    <PackageReference Include="NLog" Version="5.2.8" />
  </ItemGroup>

  <ItemGroup>
    <Resource Include="Resources\**\*" />
  </ItemGroup>
</Project>
```

2. **代码兼容性修改**：
   - 使用可空引用类型
   - 替换过时的 API
   - 使用新的语言特性（如记录类型、模式匹配）

### 3.2 功能增强计划

#### 多线程分片下载

```csharp
public class MultiThreadedDownloadService : IDownloadService
{
    private const int CHUNK_SIZE = 4 * 1024 * 1024; // 4MB 每块
    private const int MAX_PARALLEL_DOWNLOADS = 4;

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request,
        CancellationToken cancellationToken)
    {
        // 获取文件大小
        var fileSize = await GetFileSizeAsync(request.Url, cancellationToken);

        // 计算分片
        var chunks = CalculateChunks(fileSize, CHUNK_SIZE);

        // 并行下载
        var downloadTasks = chunks.Select(chunk =>
            DownloadChunkAsync(request.Url, chunk, cancellationToken));

        await Task.WhenAll(downloadTasks);

        // 合并文件
        await MergeChunksAsync(chunks, request.SavePath);

        return new DownloadResult { Success = true };
    }
}
```

#### 下载队列管理

```csharp
public class DownloadQueue : IDisposable
{
    private readonly Channel<DownloadTask> _queue;
    private readonly List<DownloadWorker> _workers;

    public DownloadQueue(int maxConcurrentDownloads = 3)
    {
        _queue = Channel.CreateUnbounded<DownloadTask>();
        _workers = Enumerable.Range(0, maxConcurrentDownloads)
            .Select(_ => new DownloadWorker(_queue.Reader))
            .ToList();
    }

    public async Task EnqueueAsync(DownloadTask task)
    {
        await _queue.Writer.WriteAsync(task);
    }
}
```

---

## 阶段四：测试策略

### 4.1 单元测试

使用 xUnit + Moq + FluentAssertions：

```csharp
// Tests/DownloadServiceTests.cs
using FluentAssertions;
using Moq;
using Moq.Protected;
using System.Net;
using Xunit;

namespace WYDownloader.Tests
{
    public class DownloadServiceTests
    {
        [Fact]
        public async Task DownloadAsync_WithValidUrl_ShouldSucceed()
        {
            // Arrange
            var mockHttp = new Mock<HttpMessageHandler>();
            mockHttp.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("test content")
                });

            var httpClient = new HttpClient(mockHttp.Object);
            var service = new DownloadService(httpClient);

            // Act
            var result = await service.DownloadAsync(new DownloadRequest
            {
                Url = "https://example.com/file.txt",
                SavePath = Path.GetTempPath()
            });

            // Assert
            result.Success.Should().BeTrue();
        }

        [Fact]
        public async Task DownloadAsync_WithCancellation_ShouldReturnCancelled()
        {
            // 测试取消逻辑
        }

        [Theory]
        [InlineData("http://example.com/file.exe")]
        [InlineData("ftp://example.com/file.txt")]
        public void DownloadAsync_WithInvalidUrl_ShouldThrow(string url)
        {
            // 测试 URL 验证
        }
    }
}
```

### 4.2 集成测试

```csharp
// Tests/Integration/DownloadIntegrationTests.cs
public class DownloadIntegrationTests : IDisposable
{
    private readonly string _testDownloadPath;

    public DownloadIntegrationTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDownloadPath);
    }

    [Fact]
    public async Task FullDownloadFlow_WithRealServer_ShouldWork()
    {
        // 使用 TestServer 或本地 HTTP 服务器
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }
}
```

---

## 实施时间表

| 阶段 | 任务 | 预计时间 | 依赖 |
|------|------|----------|------|
| **P0** | 线程安全修复 | 2天 | 无 |
| **P0** | 资源泄漏修复 | 1天 | 无 |
| **P0** | 路径验证强化 | 2天 | 无 |
| **P1** | 服务接口设计 | 3天 | P0 |
| **P1** | MVVM ViewModel | 5天 | P0 |
| **P1** | 依赖注入 | 2天 | P1 |
| **P1** | 单元测试框架 | 3天 | P1 |
| **P2** | .NET 8 迁移 | 5天 | P1 |
| **P2** | 代码现代化 | 3天 | P2 |
| **P3** | 多线程下载 | 5天 | P2 |
| **P3** | 下载队列 | 3天 | P3 |

---

## 风险评估

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|--------|------|----------|
| 迁移引入新 Bug | 中 | 高 | 完整测试覆盖，渐进式迁移 |
| 第三方库不兼容 | 低 | 中 | 提前验证，准备替代方案 |
| 用户界面回退 | 低 | 中 | 保持 UI 设计一致，充分测试 |
| 性能下降 | 低 | 高 | 性能基准测试，持续监控 |

---

## 结论

本方案提供了从当前状态到目标架构的渐进式改进路径：

1. **立即行动**：修复安全和稳定性问题
2. **短期目标**：引入 MVVM 和依赖注入
3. **长期愿景**：迁移到 .NET 8，增强功能

建议分阶段实施，每阶段完成后进行充分测试再进入下一阶段。
