# P0 问题快速修复指南

本文档提供最小化的快速修复方案，可在 1-2 小时内完成实施。

---

## 快速修复概览

| 问题 | 修复时间 | 风险等级 | 修复方式 |
|------|----------|----------|----------|
| 线程安全 | 30分钟 | 中 | 添加 `volatile` |
| 资源泄漏 | 20分钟 | 高 | 添加 `using` 语句 |
| 路径遍历 | 30分钟 | 高 | 添加路径验证 |
| 事件泄漏 | 20分钟 | 中 | 使用命名方法 |

---

## 修复步骤

### 步骤 1：修复资源泄漏（最高优先级）

#### 修改 `DownloadManager.cs` 第 205-264 行

**原代码：**

```csharp
private async Task DownloadFileInternalAsync(string url, string filePath,
    long existingBytes, CancellationToken cancellationToken)
{
    long resumeBytes = existingBytes;
    bool triedRange = resumeBytes > 0 && enableResume;
    HttpResponseMessage response = null;  // 问题：可能未释放

    while (true)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            if (enableResume && resumeBytes > 0)
            {
                request.Headers.Range = new RangeHeaderValue(resumeBytes, null);
            }
            response = await httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        // ...
    }

    using (response)  // 问题：异常时未执行到这里
    {
        // ...
    }
}
```

**修复后代码：**

```csharp
private async Task DownloadFileInternalAsync(string url, string filePath,
    long existingBytes, CancellationToken cancellationToken)
{
    long resumeBytes = existingBytes;
    bool triedRange = resumeBytes > 0 && enableResume;

    // 修复：提取为单独方法，确保 using 覆盖整个生命周期
    using (var response = await GetResponseWithRetryAsync(
        url, resumeBytes, triedRange, cancellationToken))
    {
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? 0;
        long totalExpectedBytes = totalBytes > 0 ? resumeBytes + totalBytes : 0;
        long totalBytesRead = resumeBytes;

        if (resumeBytes > 0)
        {
            OnProgressChanged(resumeBytes, totalExpectedBytes);
            lastProgressUpdateTime = DateTime.Now;
        }

        lastBytesReceived = resumeBytes;
        lastUpdateTime = DateTime.Now;

        FileMode fileMode = (enableResume && resumeBytes > 0)
            ? FileMode.Append
            : FileMode.Create;

        using (var contentStream = await response.Content.ReadAsStreamAsync())
        using (var fileStream = new FileStream(filePath, fileMode, FileAccess.Write,
            FileShare.None, DownloadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[DownloadBufferSize];
            var isMoreToRead = true;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                while (IsPaused && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken);
                }

                if (cancellationToken.IsCancellationRequested)
                    break;

                var bytesRead = await contentStream.ReadAsync(
                    buffer, 0, buffer.Length, cancellationToken);
                if (bytesRead == 0)
                {
                    isMoreToRead = false;
                    continue;
                }

                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalBytesRead += bytesRead;

                if (!IsPaused)
                {
                    var now = DateTime.Now;
                    if (now - lastProgressUpdateTime >= ProgressUpdateInterval)
                    {
                        OnProgressChanged(totalBytesRead, totalExpectedBytes);
                        lastProgressUpdateTime = now;
                    }
                }
            } while (isMoreToRead);

            if (!IsPaused)
            {
                OnProgressChanged(totalBytesRead, totalExpectedBytes);
            }
        }
    }
}

// 新增方法：带重试的响应获取
private async Task<HttpResponseMessage> GetResponseWithRetryAsync(
    string url, long resumeBytes, bool triedRange, CancellationToken cancellationToken)
{
    while (true)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            if (enableResume && resumeBytes > 0)
            {
                request.Headers.Range = new RangeHeaderValue(resumeBytes, null);
            }

            var response = await httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // 处理续传失败回退
            if (enableResume && resumeBytes > 0 &&
                (response.StatusCode == HttpStatusCode.OK ||
                 response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
            {
                response.Dispose();

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                Logger.Warn("服务端不支持续传，回退整包下载");
                resumeBytes = 0;

                if (triedRange)
                {
                    triedRange = false;
                    continue;  // 重试
                }
            }

            return response;  // 返回未释放的响应，由调用方使用 using
        }
    }
}
```

---

### 步骤 2：修复线程安全

#### 修改 `DownloadManager.cs` 第 24-26 行

**原代码：**

```csharp
public bool IsDownloading { get; private set; } = false;
public bool IsPaused { get; private set; } = false;
```

**修复后代码：**

```csharp
// 添加 volatile 确保线程可见性
private volatile bool _isDownloading;
private volatile bool _isPaused;

public bool IsDownloading
{
    get => _isDownloading;
    private set => _isDownloading = value;
}

public bool IsPaused
{
    get => _isPaused;
    private set => _isPaused = value;
}
```

#### 修改 `DownloadManager.cs` 第 58-73 行（PauseResume 方法）

**原代码：**

```csharp
public void PauseResume()
{
    if (!IsDownloading)
        return;

    if (IsPaused)
    {
        IsPaused = false;
        downloadStopwatch.Start();
    }
    else
    {
        IsPaused = true;
        downloadStopwatch.Stop();
    }
}
```

**修复后代码：**

```csharp
// 添加同步原语
private readonly ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
private readonly object _pauseLock = new object();

public void PauseResume()
{
    if (!IsDownloading)
        return;

    lock (_pauseLock)  // 确保状态变更原子性
    {
        if (IsPaused)
        {
            IsPaused = false;
            _pauseEvent.Set();  // 通知等待线程继续
            downloadStopwatch.Start();
            Logger.Info("下载已恢复");
        }
        else
        {
            IsPaused = true;
            _pauseEvent.Reset();  // 阻止等待线程
            downloadStopwatch.Stop();
            Logger.Info("下载已暂停");
        }
    }
}
```

#### 修改 `DownloadManager.cs` 第 295-298 行（忙等待循环）

**原代码：**

```csharp
while (IsPaused && !cancellationToken.IsCancellationRequested)
{
    await Task.Delay(100, cancellationToken);
}
```

**修复后代码：**

```csharp
// 使用 ManualResetEventSlim 替代忙等待
if (IsPaused)
{
    await Task.Run(() => _pauseEvent.Wait(cancellationToken), cancellationToken);
}
```

---

### 步骤 3：修复路径遍历漏洞

#### 修改 `DownloadManager.cs` 第 355-411 行（ExtractZipFileAsync）

**在方法开头添加路径验证：**

```csharp
public static async Task ExtractZipFileAsync(string zipFilePath, string extractPath,
    IProgress<ExtractionProgress> progress, CancellationToken cancellationToken)
{
    // 验证目标路径
    extractPath = Path.GetFullPath(extractPath);
    Directory.CreateDirectory(extractPath);

    await Task.Run(() =>
    {
        string extractDir = Path.Combine(extractPath,
            Path.GetFileNameWithoutExtension(zipFilePath));

        // 处理目录冲突
        if (Directory.Exists(extractDir))
        {
            int counter = 1;
            string originalDir = extractDir;
            while (Directory.Exists(extractDir))
            {
                extractDir = originalDir + "_" + counter;
                counter++;
            }
        }

        Directory.CreateDirectory(extractDir);

        // 验证并规范化根路径
        string extractRoot = Path.GetFullPath(extractDir);
        if (!extractRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
        {
            extractRoot += Path.DirectorySeparatorChar;
        }

        using (var archive = ZipFile.OpenRead(zipFilePath))
        {
            int totalEntries = archive.Entries.Count;
            int processedEntries = 0;

            foreach (var entry in archive.Entries)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                // 修复：安全路径验证
                string destinationPath = Path.GetFullPath(
                    Path.Combine(extractDir, entry.FullName));

                // 严格的路径遍历检查
                if (!destinationPath.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"阻止路径遍历攻击: {entry.FullName}");
                    continue;  // 跳过恶意条目
                }

                // 检查危险扩展名
                string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                string[] dangerousExts = { ".exe", ".dll", ".bat", ".cmd", ".sh", ".ps1" };
                if (dangerousExts.Contains(extension))
                {
                    Logger.Warn($"阻止危险文件: {entry.Name}");
                    continue;
                }

                // 检查文件大小（防止解压炸弹）
                const long MAX_FILE_SIZE = 100 * 1024 * 1024;  // 100MB
                if (entry.Length > MAX_FILE_SIZE)
                {
                    Logger.Warn($"跳过大文件: {entry.Name} ({entry.Length} bytes)");
                    continue;
                }

                // 确保目录存在
                string destinationDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                // 解压文件
                entry.ExtractToFile(destinationPath, true);

                processedEntries++;
                progress?.Report(new ExtractionProgress
                {
                    CurrentEntry = processedEntries,
                    TotalEntries = totalEntries
                });
            }
        }
    }, cancellationToken);
}
```

---

### 步骤 4：修复事件泄漏

#### 修改 `MainWindow.xaml.cs` 第 459-485 行

**原代码：**

```csharp
EventHandler<DownloadProgressEventArgs> progressHandler = (s, e) =>
    Dispatcher.InvokeAsync(() => { /* ... */ });

downloadManager.ProgressChanged += progressHandler;
// ...
downloadManager.ProgressChanged -= progressHandler;  // 可能无法正确移除
```

**修复后代码：**

```csharp
// 将 lambda 改为命名方法
private void OnDownloadProgress(object sender, DownloadProgressEventArgs e)
{
    Dispatcher.InvokeAsync(() =>
    {
        if (e.TotalBytes > 0)
        {
            var progressPercentage = (double)e.BytesReceived / e.TotalBytes * 100;
            progressBar.Value = progressPercentage;
            lblProgress.Text = $"{FormatBytes(e.BytesReceived)} / {FormatBytes(e.TotalBytes)} ({progressPercentage:F1}%)";
        }
        else
        {
            lblProgress.Text = "已下载：" + FormatBytes(e.BytesReceived);
        }

        if (e.Speed > 0)
        {
            lblSpeed.Text = FormatBytes(e.Speed) + "/s";
        }
    });
}

private void OnDownloadCompleted(object sender, DownloadCompletedEventArgs e)
{
    completionArgs = e;
}

private void OnDownloadError(object sender, DownloadErrorEventArgs e)
{
    downloadError = e.Exception;
}

// 使用命名方法订阅和取消订阅
downloadManager.ProgressChanged += OnDownloadProgress;
downloadManager.DownloadCompleted += OnDownloadCompleted;
downloadManager.DownloadError += OnDownloadError;

// ...

// 取消订阅
downloadManager.ProgressChanged -= OnDownloadProgress;
downloadManager.DownloadCompleted -= OnDownloadCompleted;
downloadManager.DownloadError -= OnDownloadError;
```

---

## 快速验证

### 编译检查

```bash
# 确保代码能编译通过
msbuild WYDownloader.sln /p:Configuration=Debug
```

### 功能测试清单

- [ ] 启动程序无异常
- [ ] 开始下载正常
- [ ] 暂停/恢复正常
- [ ] 取消下载正常
- [ ] ZIP 解压正常
- [ ] 解压含恶意路径的 ZIP 被阻止

---

## 完整修复代码文件

如果希望一次性替换，以下是修复后的完整 `DownloadManager.cs`：

```csharp
// Core/DownloadManager.cs
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace WYDownloader.Core
{
    public class DownloadManager : IDisposable
    {
        private const int DownloadBufferSize = 131072;
        private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromMilliseconds(300);

        private HttpClient httpClient;
        private CancellationTokenSource cancellationTokenSource;
        private Stopwatch downloadStopwatch;
        private long lastBytesReceived = 0;
        private DateTime lastUpdateTime;
        private DateTime lastProgressUpdateTime;

        // 线程安全的状态
        private volatile bool _isDownloading;
        private volatile bool _isPaused;
        private readonly ManualResetEventSlim pauseEvent = new ManualResetEventSlim(true);
        private readonly object pauseLock = new object();

        public bool IsDownloading => _isDownloading;
        public bool IsPaused => _isPaused;

        public event EventHandler<DownloadProgressEventArgs> ProgressChanged;
        public event EventHandler<DownloadCompletedEventArgs> DownloadCompleted;
        public event EventHandler<DownloadErrorEventArgs> DownloadError;

        private bool enableResume = true;

        public DownloadManager()
        {
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            downloadStopwatch = new Stopwatch();
            lastUpdateTime = DateTime.Now;
        }

        public void SetEnableResume(bool enable) => enableResume = enable;

        public void CancelDownload() => cancellationTokenSource?.Cancel();

        public void PauseResume()
        {
            if (!IsDownloading) return;

            lock (pauseLock)
            {
                if (_isPaused)
                {
                    _isPaused = false;
                    pauseEvent.Set();
                    downloadStopwatch.Start();
                }
                else
                {
                    _isPaused = true;
                    pauseEvent.Reset();
                    downloadStopwatch.Stop();
                }
            }
        }

        public async Task<bool> DownloadFileAsync(string url, string savePath)
        {
            if (_isDownloading) return false;

            Logger.Info("开始下载: " + url);
            cancellationTokenSource = new CancellationTokenSource();
            _isDownloading = true;
            _isPaused = false;
            downloadStopwatch.Start();
            lastUpdateTime = DateTime.Now;
            lastProgressUpdateTime = DateTime.MinValue;
            lastBytesReceived = 0;

            try
            {
                string fileName = Path.GetFileName(new Uri(url).LocalPath);
                if (string.IsNullOrEmpty(fileName) || !Path.HasExtension(fileName))
                    fileName = "download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

                string finalPath = Path.Combine(savePath, fileName);
                string partPath = finalPath + ".part";

                // 处理文件冲突
                (finalPath, partPath) = ResolveFileConflict(finalPath, partPath);

                // 断点续传
                long existingBytes = 0;
                if (enableResume && File.Exists(partPath))
                    existingBytes = new FileInfo(partPath).Length;

                await DownloadInternalAsync(url, partPath, existingBytes, cancellationTokenSource.Token);

                downloadStopwatch.Stop();

                if (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    if (File.Exists(partPath))
                    {
                        if (File.Exists(finalPath)) File.Delete(finalPath);
                        File.Move(partPath, finalPath);
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
                _isDownloading = false;
                _isPaused = false;
                downloadStopwatch.Reset();
                cancellationTokenSource?.Dispose();
                cancellationTokenSource = null;
            }
        }

        private async Task DownloadInternalAsync(string url, string filePath,
            long existingBytes, CancellationToken cancellationToken)
        {
            long resumeBytes = existingBytes;
            bool triedRange = resumeBytes > 0 && enableResume;

            using (var response = await GetResponseAsync(url, resumeBytes, triedRange, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? 0;
                long totalExpectedBytes = totalBytes > 0 ? resumeBytes + totalBytes : 0;

                if (resumeBytes > 0)
                {
                    OnProgressChanged(resumeBytes, totalExpectedBytes);
                    lastProgressUpdateTime = DateTime.Now;
                }

                lastBytesReceived = resumeBytes;
                lastUpdateTime = DateTime.Now;

                FileMode fileMode = (enableResume && resumeBytes > 0)
                    ? FileMode.Append
                    : FileMode.Create;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(filePath, fileMode, FileAccess.Write,
                    FileShare.None, DownloadBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[DownloadBufferSize];
                    long totalBytesRead = resumeBytes;

                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // 修复：使用 ManualResetEventSlim 替代忙等待
                        if (_isPaused)
                        {
                            await Task.Run(() => pauseEvent.Wait(cancellationToken), cancellationToken);
                        }

                        var bytesRead = await contentStream.ReadAsync(
                            buffer, 0, buffer.Length, cancellationToken);
                        if (bytesRead == 0) break;

                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        totalBytesRead += bytesRead;

                        if (!_isPaused)
                        {
                            var now = DateTime.Now;
                            if (now - lastProgressUpdateTime >= ProgressUpdateInterval)
                            {
                                OnProgressChanged(totalBytesRead, totalExpectedBytes);
                                lastProgressUpdateTime = now;
                            }
                        }
                    }

                    if (!_isPaused)
                        OnProgressChanged(totalBytesRead, totalExpectedBytes);
                }
            }
        }

        private async Task<HttpResponseMessage> GetResponseAsync(string url, long resumeBytes,
            bool triedRange, CancellationToken cancellationToken)
        {
            while (true)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (enableResume && resumeBytes > 0)
                        request.Headers.Range = new RangeHeaderValue(resumeBytes, null);

                    var response = await httpClient.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    if (enableResume && resumeBytes > 0 &&
                        (response.StatusCode == HttpStatusCode.OK ||
                         response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
                    {
                        response.Dispose();
                        if (File.Exists(url)) File.Delete(url);  // 清除断点文件
                        Logger.Warn("服务端不支持续传，回退整包下载");
                        resumeBytes = 0;
                        if (triedRange) { triedRange = false; continue; }
                    }

                    return response;
                }
            }
        }

        private void OnProgressChanged(long bytesReceived, long totalBytes)
        {
            var currentTime = DateTime.Now;
            var timeDiff = (currentTime - lastUpdateTime).TotalSeconds;
            long speed = 0;

            if (timeDiff >= 1.0)
            {
                var bytesDiff = bytesReceived - lastBytesReceived;
                speed = (long)(bytesDiff / timeDiff);
                lastBytesReceived = bytesReceived;
                lastUpdateTime = currentTime;
            }

            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
            {
                BytesReceived = bytesReceived,
                TotalBytes = totalBytes,
                Speed = speed
            });
        }

        private (string finalPath, string partPath) ResolveFileConflict(string finalPath, string partPath)
        {
            if (File.Exists(finalPath) || File.Exists(partPath))
            {
                int counter = 1;
                string nameWithoutExt = Path.GetFileNameWithoutExtension(finalPath);
                string extension = Path.GetExtension(finalPath);
                string directory = Path.GetDirectoryName(finalPath);

                do
                {
                    finalPath = Path.Combine(directory, $"{nameWithoutExt}_{counter}{extension}");
                    partPath = finalPath + ".part";
                    counter++;
                } while (File.Exists(finalPath) || File.Exists(partPath));
            }
            return (finalPath, partPath);
        }

        public static async Task ExtractZipFileAsync(string zipFilePath, string extractPath,
            IProgress<ExtractionProgress> progress, CancellationToken cancellationToken)
        {
            extractPath = Path.GetFullPath(extractPath);
            Directory.CreateDirectory(extractPath);

            await Task.Run(() =>
            {
                string extractDir = Path.Combine(extractPath,
                    Path.GetFileNameWithoutExtension(zipFilePath));

                if (Directory.Exists(extractDir))
                {
                    int counter = 1;
                    string originalDir = extractDir;
                    while (Directory.Exists(extractDir))
                    {
                        extractDir = originalDir + "_" + counter;
                        counter++;
                    }
                }

                Directory.CreateDirectory(extractDir);

                // 安全路径验证
                string extractRoot = Path.GetFullPath(extractDir);
                if (!extractRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    extractRoot += Path.DirectorySeparatorChar;

                using (var archive = ZipFile.OpenRead(zipFilePath))
                {
                    int totalEntries = archive.Entries.Count;
                    int processedEntries = 0;

                    foreach (var entry in archive.Entries)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        string destinationPath = Path.GetFullPath(
                            Path.Combine(extractDir, entry.FullName));

                        // 路径遍历防护
                        if (!destinationPath.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Warn($"阻止路径遍历: {entry.FullName}");
                            continue;
                        }

                        // 危险文件检查
                        string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                        if (new[] { ".exe", ".dll", ".bat", ".cmd" }.Contains(ext))
                        {
                            Logger.Warn($"阻止危险文件: {entry.Name}");
                            continue;
                        }

                        // 文件大小检查
                        if (entry.Length > 100 * 1024 * 1024)  // 100MB
                        {
                            Logger.Warn($"跳过大文件: {entry.Name}");
                            continue;
                        }

                        string destinationDir = Path.GetDirectoryName(destinationPath);
                        if (!Directory.Exists(destinationDir))
                            Directory.CreateDirectory(destinationDir);

                        entry.ExtractToFile(destinationPath, true);

                        processedEntries++;
                        progress?.Report(new ExtractionProgress
                        {
                            CurrentEntry = processedEntries,
                            TotalEntries = totalEntries
                        });
                    }
                }
            }, cancellationToken);
        }

        public void Dispose()
        {
            httpClient?.Dispose();
            cancellationTokenSource?.Dispose();
            pauseEvent?.Dispose();
        }
    }

    public class DownloadProgressEventArgs : EventArgs
    {
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public long Speed { get; set; }
    }

    public class DownloadCompletedEventArgs : EventArgs
    {
        public string FilePath { get; set; }
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
    }

    public class DownloadErrorEventArgs : EventArgs
    {
        public Exception Exception { get; set; }
    }

    public class ExtractionProgress
    {
        public int CurrentEntry { get; set; }
        public int TotalEntries { get; set; }
    }
}
```

---

## 回滚方案

如果修复出现问题，快速回滚步骤：

1. **使用 Git 回滚**（如果有版本控制）：
   ```bash
   git checkout HEAD -- Core/DownloadManager.cs
   ```

2. **手动备份恢复**：
   - 在修改前创建 `DownloadManager.cs.bak`
   - 出现问题时复制回原文件

3. **快速禁用功能**：
   ```csharp
   // 临时禁用自动解压
   chkAutoExtract.IsEnabled = false;
   ```
