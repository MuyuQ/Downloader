# 重构代码示例

本文档提供具体的代码重构示例，可以直接参考使用。

---

## 1. ThreadSafeBoolean 实现

```csharp
// Core/Threading/ThreadSafeBoolean.cs
namespace WYDownloader.Core.Threading
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

        public bool CompareExchange(bool expectedValue, bool newValue)
        {
            int expected = expectedValue ? 1 : 0;
            int newVal = newValue ? 1 : 0;
            return Interlocked.CompareExchange(ref _value, newVal, expected) == expected;
        }
    }
}
```

---

## 2. 安全的 ZIP 解压器

```csharp
// Core/Security/SafeZipExtractor.cs
using System.IO.Compression;

namespace WYDownloader.Core.Security
{
    /// <summary>
    /// 安全的 ZIP 文件解压器
    /// </summary>
    public class SafeZipExtractor
    {
        private readonly ILogger _logger;
        private readonly ZipSecurityOptions _options;

        public SafeZipExtractor(ILogger logger, ZipSecurityOptions options = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? new ZipSecurityOptions();
        }

        /// <summary>
        /// 安全地解压 ZIP 文件
        /// </summary>
        public async Task<ExtractionResult> ExtractAsync(
            string zipPath,
            string extractPath,
            IProgress<ExtractionProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            var result = new ExtractionResult { Success = true };

            try
            {
                using var archive = ZipFile.OpenRead(zipPath);

                // 安全检查
                if (!await ValidateArchiveAsync(archive))
                {
                    result.Success = false;
                    result.ErrorMessage = "ZIP 文件安全检查失败";
                    return result;
                }

                var entries = archive.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name))
                    .ToList();

                int processedCount = 0;

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryGetSafeExtractPath(entry, extractPath, out var safePath))
                    {
                        _logger.Warn($"跳过不安全的条目: {entry.FullName}");
                        continue;
                    }

                    await ExtractEntryAsync(entry, safePath, cancellationToken);

                    processedCount++;
                    progress?.Report(new ExtractionProgress
                    {
                        CurrentEntry = processedCount,
                        TotalEntries = entries.Count,
                        CurrentFileName = entry.Name
                    });
                }

                result.ExtractedFiles = processedCount;
                result.ExtractPath = extractPath;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                _logger.Error("解压失败", ex);
            }

            return result;
        }

        private bool TryGetSafeExtractPath(
            ZipArchiveEntry entry,
            string extractRoot,
            out string safePath)
        {
            safePath = null;

            // 路径遍历检查
            string destinationPath = Path.GetFullPath(
                Path.Combine(extractRoot, entry.FullName));

            string fullExtractRoot = Path.GetFullPath(extractRoot).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!destinationPath.StartsWith(fullExtractRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 危险扩展名检查
            string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (_options.BlockedExtensions.Contains(extension))
            {
                return false;
            }

            // 文件大小检查
            if (entry.Length > _options.MaxFileSize)
            {
                return false;
            }

            safePath = destinationPath;
            return true;
        }

        private async Task<bool> ValidateArchiveAsync(ZipArchive archive)
        {
            long totalSize = archive.Entries.Sum(e => e.Length);
            long compressedSize = archive.Entries.Sum(e => e.CompressedLength);

            // 压缩炸弹检查
            if (compressedSize > 0)
            {
                double ratio = (double)totalSize / compressedSize;
                if (ratio > _options.MaxCompressionRatio)
                {
                    _logger.Error($"检测到潜在的 ZIP 炸弹: 压缩比 {ratio:F1}");
                    return false;
                }
            }

            // 总大小检查
            if (totalSize > _options.MaxTotalSize)
            {
                _logger.Error($"ZIP 内容过大: {totalSize} bytes");
                return false;
            }

            // 条目数量检查
            if (archive.Entries.Count > _options.MaxEntries)
            {
                _logger.Error($"ZIP 条目过多: {archive.Entries.Count}");
                return false;
            }

            return true;
        }

        private async Task ExtractEntryAsync(
            ZipArchiveEntry entry,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            string directory = Path.GetDirectoryName(destinationPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 使用流式复制避免内存占用
            using var sourceStream = entry.Open();
            using var destStream = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous);

            await sourceStream.CopyToAsync(destStream, cancellationToken);
        }
    }

    public class ZipSecurityOptions
    {
        public long MaxFileSize { get; set; } = 100 * 1024 * 1024; // 100MB
        public long MaxTotalSize { get; set; } = 500 * 1024 * 1024; // 500MB
        public int MaxEntries { get; set; } = 10000;
        public double MaxCompressionRatio { get; set; } = 10.0;

        public HashSet<string> BlockedExtensions { get; set; } = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".bat", ".cmd", ".sh", ".ps1",
            ".vbs", ".js", ".wsf", ".hta", ".scr"
        };
    }

    public class ExtractionResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int ExtractedFiles { get; set; }
        public string ExtractPath { get; set; }
    }
}
```

---

## 3. 重构后的 DownloadService

```csharp
// Services/DownloadService.cs
using System.IO;
using System.Net.Http.Headers;

namespace WYDownloader.Services
{
    /// <summary>
    /// 下载服务实现
    /// </summary>
    public class DownloadService : IDownloadService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly DownloadOptions _options;

        private CancellationTokenSource _cancellationTokenSource;
        private FileStream _fileStream;

        private readonly ThreadSafeBoolean _isDownloading = new ThreadSafeBoolean();
        private readonly ThreadSafeBoolean _isPaused = new ThreadSafeBoolean();
        private readonly SemaphoreSlim _pauseSemaphore = new SemaphoreSlim(1, 1);

        public bool IsDownloading => _isDownloading.Value;
        public bool IsPaused => _isPaused.Value;

        public event EventHandler<DownloadProgressInfo> ProgressChanged;
        public event EventHandler<DownloadCompletedInfo> DownloadCompleted;
        public event EventHandler<DownloadErrorInfo> DownloadError;

        public DownloadService(HttpClient httpClient, ILogger logger, DownloadOptions options = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? new DownloadOptions();
        }

        public async Task<DownloadResult> DownloadAsync(
            DownloadRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_isDownloading.Value)
                throw new InvalidOperationException("已有下载任务正在进行");

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var linkedToken = _cancellationTokenSource.Token;

            try
            {
                _isDownloading.Value = true;

                var fileName = GetFileName(request);
                var finalPath = Path.Combine(request.SavePath, fileName);
                var partPath = finalPath + ".part";

                // 处理文件冲突
                (finalPath, partPath) = ResolveFileConflict(finalPath, partPath, request.EnableResume);

                // 获取已下载大小
                long existingBytes = GetExistingFileSize(partPath, request.EnableResume);

                // 执行下载
                await DownloadInternalAsync(request, partPath, existingBytes, linkedToken);

                // 移动文件
                if (File.Exists(partPath))
                {
                    File.Move(partPath, finalPath, true);
                }

                return new DownloadResult
                {
                    Success = true,
                    FilePath = finalPath
                };
            }
            catch (OperationCanceledException)
            {
                return new DownloadResult
                {
                    Success = false,
                    Cancelled = true
                };
            }
            catch (Exception ex)
            {
                _logger.Error("下载失败", ex);
                DownloadError?.Invoke(this, new DownloadErrorInfo { Exception = ex });

                return new DownloadResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
            finally
            {
                Cleanup();
            }
        }

        private async Task DownloadInternalAsync(
            DownloadRequest request,
            string filePath,
            long existingBytes,
            CancellationToken cancellationToken)
        {
            using var response = await GetResponseAsync(request, existingBytes, cancellationToken);

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            var totalExpectedBytes = totalBytes > 0 ? existingBytes + totalBytes : 0;

            var fileMode = existingBytes > 0 && request.EnableResume
                ? FileMode.Append
                : FileMode.Create;

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(
                filePath,
                fileMode,
                FileAccess.Write,
                FileShare.None,
                _options.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[_options.BufferSize];
            long totalBytesRead = existingBytes;
            var stopwatch = Stopwatch.StartNew();
            var lastReportTime = DateTime.MinValue;

            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                // 处理暂停
                if (_isPaused.Value)
                {
                    await _pauseSemaphore.WaitAsync(cancellationToken);
                    _pauseSemaphore.Release();
                }

                cancellationToken.ThrowIfCancellationRequested();

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalBytesRead += bytesRead;

                // 报告进度
                var now = DateTime.Now;
                if ((now - lastReportTime).TotalMilliseconds >= _options.ProgressIntervalMs)
                {
                    var speed = CalculateSpeed(totalBytesRead, existingBytes, stopwatch.Elapsed);
                    ReportProgress(totalBytesRead, totalExpectedBytes, speed);
                    lastReportTime = now;
                }
            }

            // 最终进度报告
            ReportProgress(totalBytesRead, totalExpectedBytes, 0);
        }

        private async Task<HttpResponseMessage> GetResponseAsync(
            DownloadRequest request,
            long resumeBytes,
            CancellationToken cancellationToken)
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);

            if (resumeBytes > 0 && request.EnableResume)
            {
                httpRequest.Headers.Range = new RangeHeaderValue(resumeBytes, null);
            }

            // 添加自定义请求头
            if (request.Headers != null)
            {
                foreach (var header in request.Headers)
                {
                    httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }

        public void Pause()
        {
            if (!_isDownloading.Value) return;

            _isPaused.Value = true;
            _pauseSemaphore.Wait();
        }

        public void Resume()
        {
            if (!_isDownloading.Value) return;

            _isPaused.Value = false;
            _pauseSemaphore.Release();
        }

        public void Cancel()
        {
            _cancellationTokenSource?.Cancel();
        }

        private void Cleanup()
        {
            _isDownloading.Value = false;
            _isPaused.Value = false;

            _fileStream?.Dispose();
            _fileStream = null;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        private void ReportProgress(long bytesReceived, long totalBytes, long speed)
        {
            ProgressChanged?.Invoke(this, new DownloadProgressInfo
            {
                BytesReceived = bytesReceived,
                TotalBytes = totalBytes,
                Speed = speed
            });
        }

        private long CalculateSpeed(long totalBytes, long startBytes, TimeSpan elapsed)
        {
            if (elapsed.TotalSeconds <= 0) return 0;

            var bytesTransferred = totalBytes - startBytes;
            return (long)(bytesTransferred / elapsed.TotalSeconds);
        }

        private string GetFileName(DownloadRequest request)
        {
            if (!string.IsNullOrEmpty(request.FileName))
                return request.FileName;

            try
            {
                var uri = new Uri(request.Url);
                var fileName = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrEmpty(fileName) && Path.HasExtension(fileName))
                    return fileName;
            }
            catch { }

            return $"download_{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        private (string finalPath, string partPath) ResolveFileConflict(
            string finalPath, string partPath, bool enableResume)
        {
            if (!File.Exists(finalPath) && (!File.Exists(partPath) || !enableResume))
                return (finalPath, partPath);

            int counter = 1;
            var nameWithoutExt = Path.GetFileNameWithoutExtension(finalPath);
            var extension = Path.GetExtension(finalPath);
            var directory = Path.GetDirectoryName(finalPath);

            do
            {
                finalPath = Path.Combine(directory, $"{nameWithoutExt}_{counter}{extension}");
                partPath = finalPath + ".part";
                counter++;
            } while (File.Exists(finalPath) || (File.Exists(partPath) && !enableResume));

            return (finalPath, partPath);
        }

        private long GetExistingFileSize(string partPath, bool enableResume)
        {
            if (!enableResume || !File.Exists(partPath))
                return 0;

            try
            {
                return new FileInfo(partPath).Length;
            }
            catch
            {
                return 0;
            }
        }

        public void Dispose()
        {
            Cleanup();
            _pauseSemaphore?.Dispose();
        }
    }

    public class DownloadOptions
    {
        public int BufferSize { get; set; } = 131072; // 128KB
        public int ProgressIntervalMs { get; set; } = 300;
    }
}
```

---

## 4. MVVM ViewModel 基类

```csharp
// ViewModels/ViewModelBase.cs
using CommunityToolkit.Mvvm.ComponentModel;
using System.Threading.Tasks;

namespace WYDownloader.ViewModels
{
    /// <summary>
    /// ViewModel 基类
    /// </summary>
    public abstract class ViewModelBase : ObservableObject
    {
        protected bool SetProperty<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    /// <summary>
    /// 带加载状态的 ViewModel
    /// </summary>
    public abstract class ViewModelBase : ObservableObject, IDisposable
    {
        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _loadingMessage;

        [ObservableProperty]
        private string _errorMessage;

        protected async Task<T> RunWithLoadingAsync<T>(
            Func<Task<T>> operation,
            string loadingMessage = null)
        {
            IsLoading = true;
            LoadingMessage = loadingMessage;
            ErrorMessage = null;

            try
            {
                return await operation();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                throw;
            }
            finally
            {
                IsLoading = false;
                LoadingMessage = null;
            }
        }

        protected async Task RunWithLoadingAsync(
            Func<Task> operation,
            string loadingMessage = null)
        {
            await RunWithLoadingAsync(async () =>
            {
                await operation();
                return true;
            }, loadingMessage);
        }

        public virtual void Dispose()
        {
            // 子类重写
        }
    }
}
```

---

## 5. 配置文件服务

```csharp
// Services/ConfigurationService.cs
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace WYDownloader.Services
{
    /// <summary>
    /// 强类型的配置服务
    /// </summary>
    public class ConfigurationService : IConfigurationService
    {
        private readonly string _configPath;
        private readonly ILogger<ConfigurationService> _logger;
        private AppConfiguration _configuration;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        public ConfigurationService(ILogger<ConfigurationService> logger)
        {
            _logger = logger;
            _configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WYDownloader",
                "config.json");

            LoadConfiguration();
        }

        public AppConfiguration Configuration
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    return _configuration;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        public string GetDefaultDownloadPath() =>
            Configuration.Download?.DefaultPath ?? GetDefaultPath();

        public bool GetEnableResume() =>
            Configuration.Download?.EnableResume ?? true;

        public int GetMaxConcurrentDownloads() =>
            Configuration.Download?.MaxConcurrentDownloads ?? 3;

        public void UpdateConfiguration(Action<AppConfiguration> updateAction)
        {
            _lock.EnterWriteLock();
            try
            {
                updateAction(_configuration);
                SaveConfiguration();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private void LoadConfiguration()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _configuration = JsonSerializer.Deserialize<AppConfiguration>(json)
                        ?? new AppConfiguration();
                }
                else
                {
                    _configuration = new AppConfiguration();
                    SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载配置失败");
                _configuration = new AppConfiguration();
            }
        }

        private void SaveConfiguration()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath));

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(_configuration, options);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存配置失败");
            }
        }

        private static string GetDefaultPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>
    /// 应用配置模型
    /// </summary>
    public class AppConfiguration
    {
        public DownloadSettings Download { get; set; } = new DownloadSettings();
        public NetworkSettings Network { get; set; } = new NetworkSettings();
        public UiSettings UI { get; set; } = new UiSettings();
        public List<DownloadItem> PresetDownloads { get; set; } = new List<DownloadItem>();
    }

    public class DownloadSettings
    {
        public string DefaultPath { get; set; }
        public bool EnableResume { get; set; } = true;
        public bool AutoExtract { get; set; } = true;
        public int MaxConcurrentDownloads { get; set; } = 3;
        public long MaxFileSize { get; set; } = 1024 * 1024 * 1024; // 1GB
    }

    public class NetworkSettings
    {
        public int TimeoutSeconds { get; set; } = 1800;
        public int RetryCount { get; set; } = 3;
        public string UserAgent { get; set; } =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
    }

    public class UiSettings
    {
        public int WindowWidth { get; set; } = 900;
        public int WindowHeight { get; set; } = 600;
        public string Theme { get; set; } = "Dark";
    }

    public class DownloadItem
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Description { get; set; }
    }
}
```

---

## 6. 使用示例

```csharp
// 主窗口初始化示例
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 绑定关闭事件
        Closing += async (s, e) =>
        {
            if (viewModel.IsDownloading)
            {
                e.Cancel = true;
                var result = MessageBox.Show(
                    "正在下载中，确定要退出吗？",
                    "确认",
                    MessageBoxButton.YesNo);

                if (result == MessageBoxResult.Yes)
                {
                    await viewModel.CancelDownloadAsync();
                    Close();
                }
            }
        };
    }
}
```

---

## 7. XAML 绑定示例

```xml
<!-- 主窗口 XAML -->
<Window x:Class="WYDownloader.MainWindow"
        xmlns:vm="clr-namespace:WYDownloader.ViewModels"
        Title="{Binding Title}">

    <Window.DataContext>
        <vm:MainViewModel />
    </Window.DataContext>

    <Grid>
        <!-- 下载列表 -->
        <ComboBox ItemsSource="{Binding DownloadItems}"
                  SelectedItem="{Binding SelectedItem}"
                  DisplayMemberPath="Name" />

        <!-- 进度条 -->
        <ProgressBar Value="{Binding CurrentProgress}"
                     Maximum="100" />

        <!-- 状态显示 -->
        <TextBlock Text="{Binding StatusMessage}" />

        <!-- 控制按钮 -->
        <Button Content="开始下载"
                Command="{Binding StartDownloadCommand}"
                IsEnabled="{Binding StartDownloadCommand.CanExecute}" />

        <Button Content="取消"
                Command="{Binding CancelDownloadCommand}"
                Visibility="{Binding IsDownloading, Converter={StaticResource BooleanToVisibilityConverter}}" />

        <Button Content="暂停/继续"
                Command="{Binding TogglePauseCommand}"
                Visibility="{Binding IsDownloading, Converter={StaticResource BooleanToVisibilityConverter}}" />
    </Grid>
</Window>
```
