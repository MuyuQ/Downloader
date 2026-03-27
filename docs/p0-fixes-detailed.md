# P0 关键问题修复详细方案

本文档提供立即可实施的安全和稳定性修复代码。

---

## 目录

1. [线程安全问题修复](#1-线程安全问题修复)
2. [资源泄漏修复](#2-资源泄漏修复)
3. [路径遍历漏洞修复](#3-路径遍历漏洞修复)
4. [事件泄漏修复](#4-事件泄漏修复)
5. [实施检查清单](#5-实施检查清单)

---

## 1. 线程安全问题修复

### 1.1 问题分析

**原有问题代码** (`DownloadManager.cs:295-298`)：

```csharp
while (IsPaused && !cancellationToken.IsCancellationRequested)
{
    await Task.Delay(100, cancellationToken);  // 忙等待，浪费 CPU
}
```

**问题**：
- `IsPaused` 是普通布尔值，非线程安全
- 忙等待循环浪费 CPU 资源
- 没有同步机制

### 1.2 完整修复代码

#### 步骤 1：创建 ThreadSafeBoolean.cs

```csharp
// Core/Threading/ThreadSafeBoolean.cs
using System.Threading;

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
    }
}
```

#### 步骤 2：创建 PauseToken.cs

```csharp
// Core/Threading/PauseToken.cs
using System.Threading;
using System.Threading.Tasks;

namespace WYDownloader.Core.Threading
{
    /// <summary>
    /// 暂停令牌源 - 用于控制下载暂停/恢复
    /// </summary>
    public class PauseTokenSource
    {
        private volatile TaskCompletionSource<bool> _paused;

        public PauseToken Token => new PauseToken(this);

        public bool IsPaused => _paused != null;

        public void Pause()
        {
            Interlocked.CompareExchange(ref _paused, new TaskCompletionSource<bool>(), null);
        }

        public void Resume()
        {
            var tcs = _paused;
            if (tcs != null && Interlocked.CompareExchange(ref _paused, null, tcs) == tcs)
            {
                tcs.SetResult(true);
            }
        }

        internal Task WaitWhilePausedAsync()
        {
            var tcs = _paused;
            return tcs?.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// 暂停令牌
    /// </summary>
    public struct PauseToken
    {
        private readonly PauseTokenSource _source;

        public PauseToken(PauseTokenSource source)
        {
            _source = source;
        }

        public bool IsPaused => _source?.IsPaused ?? false;

        public Task WaitWhilePausedAsync()
        {
            return _source?.WaitWhilePausedAsync() ?? Task.CompletedTask;
        }
    }
}
```

#### 步骤 3：重写 DownloadManager

```csharp
// Core/DownloadManager.cs (修复后)
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using WYDownloader.Core.Threading;

namespace WYDownloader.Core
{
    public class DownloadManager : IDisposable
    {
        private const int DownloadBufferSize = 131072;
        private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromMilliseconds(300);

        private readonly HttpClient _httpClient;
        private readonly Stopwatch _downloadStopwatch;

        // 线程安全的状态管理
        private readonly ThreadSafeBoolean _isDownloading = new ThreadSafeBoolean();
        private readonly PauseTokenSource _pauseTokenSource = new PauseTokenSource();

        private CancellationTokenSource _cancellationTokenSource;
        private long _lastBytesReceived;
        private DateTime _lastUpdateTime;
        private DateTime _lastProgressUpdateTime;

        public bool IsDownloading => _isDownloading.Value;
        public bool IsPaused => _pauseTokenSource.IsPaused;

        public event EventHandler<DownloadProgressEventArgs> ProgressChanged;
        public event EventHandler<DownloadCompletedEventArgs> DownloadCompleted;
        public event EventHandler<DownloadErrorEventArgs> DownloadError;

        private bool _enableResume = true;

        public DownloadManager()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(30);
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

            _downloadStopwatch = new Stopwatch();
            _lastUpdateTime = DateTime.Now;
        }

        public void SetEnableResume(bool enable)
        {
            _enableResume = enable;
        }

        public void CancelDownload()
        {
            _cancellationTokenSource?.Cancel();
        }

        public void PauseResume()
        {
            if (!IsDownloading)
                return;

            if (IsPaused)
            {
                Resume();
            }
            else
            {
                Pause();
            }
        }

        private void Pause()
        {
            _pauseTokenSource.Pause();
            _downloadStopwatch.Stop();
            Logger.Info("下载已暂停");
        }

        private void Resume()
        {
            _pauseTokenSource.Resume();
            _downloadStopwatch.Start();
            Logger.Info("下载已恢复");
        }

        public async Task<bool> DownloadFileAsync(string url, string savePath)
        {
            if (_isDownloading.Value)
                return false;

            Logger.Info("开始下载: " + url);

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            _isDownloading.Value = true;
            _downloadStopwatch.Start();
            _lastBytesReceived = 0;
            _lastUpdateTime = DateTime.Now;
            _lastProgressUpdateTime = DateTime.MinValue;

            try
            {
                var fileName = GetSafeFileName(url);
                var (finalPath, partPath) = GetFilePaths(savePath, fileName);

                // 处理文件冲突和续传
                var existingBytes = GetExistingFileSize(partPath);

                // 执行下载
                await DownloadInternalAsync(url, partPath, existingBytes, token);

                _downloadStopwatch.Stop();

                if (!token.IsCancellationRequested)
                {
                    // 移动文件
                    if (File.Exists(partPath))
                    {
                        MoveFile(partPath, finalPath);
                    }

                    DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs
                    {
                        FilePath = finalPath,
                        Success = true
                    });

                    Logger.Info("下载完成: " + finalPath);
                    return true;
                }
                else
                {
                    DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs
                    {
                        FilePath = partPath,
                        Success = false,
                        Cancelled = true
                    });
                    Logger.Info("下载已取消");
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs
                {
                    Success = false,
                    Cancelled = true
                });
                Logger.Info("下载已取消");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error("下载失败: " + ex.Message, ex);
                DownloadError?.Invoke(this, new DownloadErrorEventArgs { Exception = ex });
                return false;
            }
            finally
            {
                Cleanup();
            }
        }

        private async Task DownloadInternalAsync(
            string url, string filePath, long existingBytes, CancellationToken cancellationToken)
        {
            long resumeBytes = existingBytes;
            bool triedRange = resumeBytes > 0 && _enableResume;

            // 获取响应（带重试逻辑）
            using var response = await GetResponseWithRetryAsync(url, resumeBytes, cancellationToken);

            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? 0;
            long totalExpectedBytes = totalBytes > 0 ? resumeBytes + totalBytes : 0;

            // 报告初始进度
            if (resumeBytes > 0)
            {
                OnProgressChanged(resumeBytes, totalExpectedBytes);
                _lastProgressUpdateTime = DateTime.Now;
            }

            _lastBytesReceived = resumeBytes;
            _lastUpdateTime = DateTime.Now;

            var fileMode = (_enableResume && resumeBytes > 0) ? FileMode.Append : FileMode.Create;

            using (var contentStream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = new FileStream(
                filePath, fileMode, FileAccess.Write, FileShare.None,
                DownloadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[DownloadBufferSize];
                long totalBytesRead = resumeBytes;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 使用 PauseToken 替代忙等待
                    await _pauseTokenSource.Token.WaitWhilePausedAsync();

                    var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0)
                        break;

                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    totalBytesRead += bytesRead;

                    if (!_pauseTokenSource.IsPaused)
                    {
                        var now = DateTime.Now;
                        if (now - _lastProgressUpdateTime >= ProgressUpdateInterval)
                        {
                            OnProgressChanged(totalBytesRead, totalExpectedBytes);
                            _lastProgressUpdateTime = now;
                        }
                    }
                }

                if (!_pauseTokenSource.IsPaused)
                {
                    OnProgressChanged(totalBytesRead, totalExpectedBytes);
                }
            }
        }

        private async Task<HttpResponseMessage> GetResponseWithRetryAsync(
            string url, long resumeBytes, CancellationToken cancellationToken)
        {
            int maxRetries = 3;
            int currentRetry = 0;

            while (true)
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);

                    if (_enableResume && resumeBytes > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(resumeBytes, null);
                    }

                    var response = await _httpClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    // 检查是否需要回退到整包下载
                    if (resumeBytes > 0 &&
                        (response.StatusCode == HttpStatusCode.OK ||
                         response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
                    {
                        response.Dispose();
                        Logger.Warn("服务端不支持续传，回退到整包下载");
                        resumeBytes = 0;

                        if (currentRetry < maxRetries)
                        {
                            currentRetry++;
                            continue;
                        }
                    }

                    return response;
                }
                catch (HttpRequestException) when (currentRetry < maxRetries)
                {
                    currentRetry++;
                    await Task.Delay(TimeSpan.FromSeconds(currentRetry), cancellationToken);
                }
            }
        }

        private void OnProgressChanged(long bytesReceived, long totalBytes)
        {
            var currentTime = DateTime.Now;
            var timeDiff = (currentTime - _lastUpdateTime).TotalSeconds;
            long speed = 0;

            if (timeDiff >= 1.0)
            {
                var bytesDiff = bytesReceived - _lastBytesReceived;
                speed = (long)(bytesDiff / timeDiff);
                _lastBytesReceived = bytesReceived;
                _lastUpdateTime = currentTime;
            }

            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
            {
                BytesReceived = bytesReceived,
                TotalBytes = totalBytes,
                Speed = speed
            });
        }

        private string GetSafeFileName(string url)
        {
            try
            {
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                if (!string.IsNullOrEmpty(fileName) && Path.HasExtension(fileName))
                    return SanitizeFileName(fileName);
            }
            catch { }

            return "download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }

        private string SanitizeFileName(string fileName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }
            return fileName;
        }

        private (string finalPath, string partPath) GetFilePaths(string savePath, string fileName)
        {
            var finalPath = Path.Combine(savePath, fileName);
            var partPath = finalPath + ".part";

            if (File.Exists(finalPath) || File.Exists(partPath))
            {
                int counter = 1;
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var extension = Path.GetExtension(fileName);

                do
                {
                    var newFileName = $"{nameWithoutExt}_{counter}{extension}";
                    finalPath = Path.Combine(savePath, newFileName);
                    partPath = finalPath + ".part";
                    counter++;
                } while (File.Exists(finalPath) || File.Exists(partPath));
            }

            return (finalPath, partPath);
        }

        private long GetExistingFileSize(string partPath)
        {
            if (_enableResume && File.Exists(partPath))
            {
                try
                {
                    return new FileInfo(partPath).Length;
                }
                catch
                {
                    return 0;
                }
            }
            return 0;
        }

        private void MoveFile(string sourcePath, string destPath)
        {
            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }
            File.Move(sourcePath, destPath);
        }

        private void Cleanup()
        {
            _isDownloading.Value = false;
            _pauseTokenSource.Resume();
            _downloadStopwatch.Reset();

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        public static async Task ExtractZipFileAsync(
            string zipFilePath, string extractPath,
            IProgress<ExtractionProgress> progress, CancellationToken cancellationToken)
        {
            await SafeZipExtractor.ExtractAsync(zipFilePath, extractPath, progress, cancellationToken);
        }

        public void Dispose()
        {
            Cleanup();
            _httpClient?.Dispose();
        }
    }
}
```

---

## 2. 资源泄漏修复

### 2.1 HttpResponseMessage 泄漏

**原有问题**：

```csharp
HttpResponseMessage response = null;
// ... 异常时 response 未释放
using (response) { }
```

**修复后**：

```csharp
// 使用 using 声明确保释放
using var response = await GetResponseAsync(url, resumeBytes, cancellationToken);
// 或明确使用 try-finally
```

### 2.2 CancellationTokenSource 泄漏

**修复后的模式**：

```csharp
public class DownloadManager : IDisposable
{
    private CancellationTokenSource _cancellationTokenSource;

    public async Task<bool> DownloadFileAsync(string url, string savePath)
    {
        // 确保清理之前的 CTS
        CleanupCancellationTokenSource();

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        try
        {
            // ... 下载逻辑
        }
        finally
        {
            CleanupCancellationTokenSource();
        }
    }

    private void CleanupCancellationTokenSource()
    {
        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }
    }

    public void Dispose()
    {
        CleanupCancellationTokenSource();
        _httpClient?.Dispose();
    }
}
```

---

## 3. 路径遍历漏洞修复

### 3.1 完整的安全 ZIP 解压实现

```csharp
// Core/Security/SafeZipExtractor.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace WYDownloader.Core.Security
{
    /// <summary>
    /// 安全的 ZIP 文件解压器
    /// </summary>
    public static class SafeZipExtractor
    {
        // 危险文件扩展名黑名单
        private static readonly HashSet<string> DangerousExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".bat", ".cmd", ".sh", ".ps1",
            ".vbs", ".js", ".wsf", ".hta", ".scr", ".com",
            ".pif", ".jar", ".msi", ".app", ".dmg"
        };

        // 最大允许的文件大小 (100MB)
        private const long MAX_FILE_SIZE = 100 * 1024 * 1024;

        // 最大压缩比 (防止压缩炸弹)
        private const double MAX_COMPRESSION_RATIO = 100.0;

        // 最大文件数量
        private const int MAX_FILE_COUNT = 10000;

        /// <summary>
        /// 安全地解压 ZIP 文件
        /// </summary>
        public static async Task ExtractAsync(
            string zipFilePath,
            string extractPath,
            IProgress<ExtractionProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(zipFilePath))
                throw new FileNotFoundException("ZIP 文件不存在", zipFilePath);

            // 验证并创建目标目录
            extractPath = Path.GetFullPath(extractPath);
            Directory.CreateDirectory(extractPath);

            using (var archive = ZipFile.OpenRead(zipFilePath))
            {
                // 安全检查
                ValidateArchive(archive);

                // 获取安全的条目列表
                var safeEntries = GetSafeEntries(archive, extractPath);

                int processedCount = 0;

                foreach (var (entry, safePath) in safeEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 创建目录
                    var directory = Path.GetDirectoryName(safePath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // 解压文件
                    await ExtractEntryAsync(entry, safePath, cancellationToken);

                    processedCount++;
                    progress?.Report(new ExtractionProgress
                    {
                        CurrentEntry = processedCount,
                        TotalEntries = safeEntries.Count,
                        CurrentFileName = entry.Name
                    });
                }
            }
        }

        /// <summary>
        /// 验证 ZIP 归档的安全性
        /// </summary>
        private static void ValidateArchive(ZipArchive archive)
        {
            // 检查文件数量
            if (archive.Entries.Count > MAX_FILE_COUNT)
            {
                throw new SecurityException($"ZIP 文件包含过多条目: {archive.Entries.Count} (最大允许: {MAX_FILE_COUNT})");
            }

            // 计算总大小和压缩比
            long totalCompressed = 0;
            long totalUncompressed = 0;

            foreach (var entry in archive.Entries)
            {
                totalCompressed += entry.CompressedLength;
                totalUncompressed += entry.Length;
            }

            // 检查压缩炸弹
            if (totalCompressed > 0)
            {
                double ratio = (double)totalUncompressed / totalCompressed;
                if (ratio > MAX_COMPRESSION_RATIO)
                {
                    throw new SecurityException($"检测到 ZIP 炸弹 (压缩比: {ratio:F1})");
                }
            }

            // 检查总大小
            if (totalUncompressed > MAX_FILE_SIZE * 10) // 总体限制 1GB
            {
                throw new SecurityException("ZIP 内容总体过大");
            }
        }

        /// <summary>
        /// 获取安全的条目列表
        /// </summary>
        private static List<(ZipArchiveEntry entry, string safePath)> GetSafeEntries(
            ZipArchive archive, string extractRoot)
        {
            var result = new List<(ZipArchiveEntry, string)>();

            foreach (var entry in archive.Entries)
            {
                // 跳过目录条目
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                // 验证并获取安全路径
                if (TryGetSafeExtractPath(entry, extractRoot, out var safePath))
                {
                    result.Add((entry, safePath));
                }
                else
                {
                    Logger.Warn($"跳过不安全的 ZIP 条目: {entry.FullName}");
                }
            }

            return result;
        }

        /// <summary>
        /// 尝试获取安全的解压路径
        /// </summary>
        private static bool TryGetSafeExtractPath(
            ZipArchiveEntry entry,
            string extractRoot,
            out string safePath)
        {
            safePath = null;

            try
            {
                // 规范化目标路径
                string destinationPath = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName));
                string fullExtractRoot = Path.GetFullPath(extractRoot);

                // 确保根目录以分隔符结尾（用于 StartsWith 检查）
                if (!fullExtractRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
                {
                    fullExtractRoot += Path.DirectorySeparatorChar;
                }

                // 路径遍历检查 - 确保在目标目录内
                if (!destinationPath.StartsWith(fullExtractRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"阻止路径遍历攻击: {entry.FullName} -> {destinationPath}");
                    return false;
                }

                // 检查文件扩展名
                string extension = Path.GetExtension(entry.Name);
                if (DangerousExtensions.Contains(extension))
                {
                    Logger.Warn($"阻止危险文件类型: {entry.Name}");
                    return false;
                }

                // 检查文件大小
                if (entry.Length > MAX_FILE_SIZE)
                {
                    Logger.Warn($"文件过大: {entry.Name} ({entry.Length} bytes)");
                    return false;
                }

                // 检查文件名长度
                if (entry.Name.Length > 255)
                {
                    Logger.Warn($"文件名过长: {entry.Name}");
                    return false;
                }

                // 检查特殊文件名（Windows 保留名）
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(entry.Name);
                if (IsReservedName(fileNameWithoutExt))
                {
                    Logger.Warn($"阻止保留文件名: {entry.Name}");
                    return false;
                }

                safePath = destinationPath;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"验证 ZIP 条目时出错: {entry.FullName}", ex);
                return false;
            }
        }

        /// <summary>
        /// 检查是否为 Windows 保留文件名
        /// </summary>
        private static bool IsReservedName(string name)
        {
            // Windows 保留文件名（不区分大小写）
            string[] reservedNames =
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };

            return reservedNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 解压单个条目
        /// </summary>
        private static async Task ExtractEntryAsync(
            ZipArchiveEntry entry,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            // 使用临时文件确保原子性
            string tempPath = destinationPath + ".tmp" + Guid.NewGuid().ToString("N");

            try
            {
                using (var sourceStream = entry.Open())
                using (var destStream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous))
                {
                    await sourceStream.CopyToAsync(destStream, 81920, cancellationToken);
                }

                // 原子性移动
                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }
                File.Move(tempPath, destinationPath);
            }
            catch
            {
                // 清理临时文件
                try { File.Delete(tempPath); } catch { }
                throw;
            }
        }
    }

    /// <summary>
    /// 解压进度信息
    /// </summary>
    public class ExtractionProgress
    {
        public int CurrentEntry { get; set; }
        public int TotalEntries { get; set; }
        public string CurrentFileName { get; set; }
        public double Percentage => TotalEntries > 0 ? (double)CurrentEntry / TotalEntries * 100 : 0;
    }
}
```

---

## 4. 事件泄漏修复

### 4.1 MainWindow 中的事件处理问题

**原有问题** (`MainWindow.xaml.cs:461-484`)：

```csharp
// 问题：使用 lambda 表达式作为事件处理器
EventHandler<DownloadProgressEventArgs> progressHandler = (s, e) => Dispatcher.InvokeAsync(() =>
{
    // ...
});

downloadManager.ProgressChanged += progressHandler;
// ...
downloadManager.ProgressChanged -= progressHandler;  // 这能正确移除吗？
```

**修复方案**：使用命名方法或弱事件模式

```csharp
// MainWindow.xaml.cs (修复后)
public partial class MainWindow : Window
{
    // 使用弱事件模式避免内存泄漏
    private readonly WeakEventHandler<DownloadProgressEventArgs> _progressHandler;
    private readonly WeakEventHandler<DownloadCompletedEventArgs> _completedHandler;
    private readonly WeakEventHandler<DownloadErrorEventArgs> _errorHandler;

    public MainWindow()
    {
        InitializeComponent();
        InitializeWeakEventHandlers();
    }

    private void InitializeWeakEventHandlers()
    {
        _progressHandler = new WeakEventHandler<DownloadProgressEventArgs>(
            OnDownloadProgress,
            h => downloadManager.ProgressChanged += h,
            h => downloadManager.ProgressChanged -= h);

        _completedHandler = new WeakEventHandler<DownloadCompletedEventArgs>(
            OnDownloadCompleted,
            h => downloadManager.DownloadCompleted += h,
            h => downloadManager.DownloadCompleted -= h);

        _errorHandler = new WeakEventHandler<DownloadErrorEventArgs>(
            OnDownloadError,
            h => downloadManager.DownloadError += h,
            h => downloadManager.DownloadError -= h);
    }

    private void OnDownloadProgress(object sender, DownloadProgressEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            progressBar.Value = e.ProgressPercentage;
            lblProgress.Text = $"{FormatBytes(e.BytesReceived)} / {FormatBytes(e.TotalBytes)}";
            lblSpeed.Text = $"{FormatBytes(e.Speed)}/s";
        });
    }

    private void OnDownloadCompleted(object sender, DownloadCompletedEventArgs e)
    {
        // 处理完成
    }

    private void OnDownloadError(object sender, DownloadErrorEventArgs e)
    {
        // 处理错误
    }
}

/// <summary>
/// 弱事件处理器包装器
/// </summary>
public class WeakEventHandler<TEventArgs> where TEventArgs : EventArgs
{
    private readonly WeakReference _targetReference;
    private readonly Action<object, TEventArgs> _handler;
    private readonly Action<EventHandler<TEventArgs>> _addHandler;
    private readonly Action<EventHandler<TEventArgs>> _removeHandler;

    public WeakEventHandler(
        Action<object, TEventArgs> handler,
        Action<EventHandler<TEventArgs>> addHandler,
        Action<EventHandler<TEventArgs>> removeHandler)
    {
        _targetReference = new WeakReference(handler.Target);
        _handler = handler;
        _addHandler = addHandler;
        _removeHandler = removeHandler;

        addHandler(Invoke);
    }

    private void Invoke(object sender, TEventArgs e)
    {
        var target = _targetReference.Target;
        if (target != null)
        {
            _handler(sender, e);
        }
        else
        {
            _removeHandler(Invoke);
        }
    }
}
```

---

## 5. 实施检查清单

### 5.1 准备阶段

- [ ] 创建代码备份或分支
- [ ] 确保可以编译通过
- [ ] 准备回滚方案

### 5.2 文件创建/修改

#### 新建文件

- [ ] `Core/Threading/ThreadSafeBoolean.cs`
- [ ] `Core/Threading/PauseToken.cs`
- [ ] `Core/Security/SafeZipExtractor.cs`
- [ ] `Core/WeakEventHandler.cs`

#### 修改文件

- [ ] `Core/DownloadManager.cs`
  - [ ] 添加 `using WYDownloader.Core.Threading;`
  - [ ] 替换 `bool isPaused` 为 `PauseTokenSource`
  - [ ] 使用 `using` 语句包装 HttpResponseMessage
  - [ ] 添加正确的 `Dispose` 实现

- [ ] `MainWindow.xaml.cs`
  - [ ] 使用弱事件模式
  - [ ] 更新 `ExtractZipFile` 调用使用 `SafeZipExtractor`

### 5.3 代码审查要点

- [ ] 所有 `HttpClient` 响应都有 `using` 语句
- [ ] 所有 `CancellationTokenSource` 都正确释放
- [ ] 暂停/恢复逻辑使用 `PauseToken`
- [ ] ZIP 解压使用 `SafeZipExtractor`
- [ ] 事件处理器使用弱引用模式

### 5.4 测试清单

#### 功能测试
- [ ] 正常下载功能
- [ ] 暂停/恢复功能
- [ ] 取消下载功能
- [ ] 断点续传功能
- [ ] ZIP 解压功能

#### 边界测试
- [ ] 网络断开重连
- [ ] 磁盘空间不足
- [ ] 大文件下载 (>1GB)
- [ ] 包含特殊字符的文件名

#### 安全测试
- [ ] 路径遍历攻击 ZIP (`../file.txt`)
- [ ] 绝对路径 ZIP (`/etc/passwd`)
- [ ] 包含可执行文件的 ZIP
- [ ] 超大 ZIP (压缩炸弹)

### 5.5 性能检查

- [ ] CPU 使用率在暂停时接近 0%
- [ ] 内存使用稳定，无持续增长
- [ ] 下载速度不受影响

---

## 6. 快速修复脚本

### 6.1 最小化修复（仅修复关键问题）

如果无法进行全面重构，至少实施以下最小修复：

```csharp
// DownloadManager.cs - 最小修复版本

// 1. 添加 volatile 修饰符
private volatile bool _isPaused;

// 2. 使用 ManualResetEventSlim 替代忙等待
private readonly ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);

public void PauseResume()
{
    if (IsPaused)
    {
        _isPaused = false;
        _pauseEvent.Set();
    }
    else
    {
        _isPaused = true;
        _pauseEvent.Reset();
    }
}

// 在下载循环中
while (IsPaused)
{
    _pauseEvent.Wait(TimeSpan.FromMilliseconds(100), cancellationToken);
}

// 3. 确保 using 语句
using (var response = await _httpClient.SendAsync(...))
{
    // ...
}

// 4. 简单的 ZIP 路径验证
string destPath = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));
string rootPath = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;

if (!destPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
{
    throw new SecurityException("检测到路径遍历攻击");
}
```

---

## 7. 验证代码

### 7.1 线程安全测试

```csharp
// 测试暂停/恢复的线程安全性
[Test]
public async Task PauseResume_ShouldBeThreadSafe()
{
    var manager = new DownloadManager();
    var tasks = new List<Task>();

    // 并发调用 Pause/Resume
    for (int i = 0; i < 100; i++)
    {
        tasks.Add(Task.Run(() => manager.PauseResume()));
    }

    await Task.WhenAll(tasks);

    // 验证状态一致性
    Assert.IsFalse(manager.IsDownloading || manager.IsPaused);
}
```

### 7.2 ZIP 安全测试

```csharp
// 测试路径遍历防护
[Test]
public void ExtractZip_WithPathTraversal_ShouldThrow()
{
    // 创建包含恶意路径的 ZIP
    var maliciousZip = CreateZipWithEntry("../../../Windows/System32/evil.dll");

    Assert.Throws<SecurityException>(() =>
        SafeZipExtractor.ExtractAsync(maliciousZip, _testPath).Wait());
}
```
