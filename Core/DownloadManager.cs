using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using WYDownloader.Core.Security;
using WYDownloader.Core.Threading;

namespace WYDownloader.Core
{
    /// <summary>
    /// 下载管理器
    /// 提供 HTTP/HTTPS 文件下载功能，支持断点续传和暂停恢复
    /// </summary>
    /// <remarks>
    /// 功能特性：
    /// - 支持断点续传（Range 请求）
    /// - 支持暂停/恢复下载
    /// - 支持下载取消
    /// - 提供下载进度、速度统计
    /// - 自动重试失败请求
    /// - 线程安全的状态管理
    /// </remarks>
    public class DownloadManager : IDisposable
    {
        #region 常量

        /// <summary>
        /// 下载缓冲区大小（128KB）
        /// 较大的缓冲区可以提高下载性能
        /// </summary>
        private const int DownloadBufferSize = 131072;

        /// <summary>
        /// 进度更新最小间隔
        /// 避免过于频繁的 UI 更新
        /// </summary>
        private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromMilliseconds(300);

        #endregion

        #region 私有字段

        /// <summary>
        /// HTTP 客户端实例
        /// </summary>
        private readonly HttpClient _httpClient;

        /// <summary>
        /// 取消令牌源
        /// 用于取消下载操作
        /// </summary>
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// 下载计时器
        /// 用于计算下载速度
        /// </summary>
        private readonly Stopwatch _downloadStopwatch;

        /// <summary>
        /// 上次记录的字节数
        /// 用于计算下载速度
        /// </summary>
        private long _lastBytesReceived;

        /// <summary>
        /// 上次更新时间
        /// 用于计算下载速度
        /// </summary>
        private DateTime _lastUpdateTime;

        /// <summary>
        /// 上次进度更新时间
        /// 用于限制进度更新频率
        /// </summary>
        private DateTime _lastProgressUpdateTime;

        /// <summary>
        /// 线程安全的下载状态标志
        /// </summary>
        private readonly ThreadSafeBoolean _isDownloading = new ThreadSafeBoolean();

        /// <summary>
        /// 暂停令牌源
        /// 用于控制下载暂停/恢复
        /// </summary>
        private readonly PauseTokenSource _pauseTokenSource = new PauseTokenSource();

        /// <summary>
        /// 是否启用断点续传
        /// </summary>
        private bool _enableResume = true;

        #endregion

        #region 公共属性

        /// <summary>
        /// 获取当前是否正在下载
        /// </summary>
        public bool IsDownloading => _isDownloading.Value;

        /// <summary>
        /// 获取当前是否已暂停
        /// </summary>
        public bool IsPaused => _pauseTokenSource.IsPaused;

        #endregion

        #region 事件

        /// <summary>
        /// 下载进度变化事件
        /// 当下载进度更新时触发
        /// </summary>
        public event EventHandler<DownloadProgressEventArgs> ProgressChanged;

        /// <summary>
        /// 下载完成事件
        /// 当下载成功或取消完成时触发
        /// </summary>
        public event EventHandler<DownloadCompletedEventArgs> DownloadCompleted;

        /// <summary>
        /// 下载错误事件
        /// 当下载过程中发生错误时触发
        /// </summary>
        public event EventHandler<DownloadErrorEventArgs> DownloadError;

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化下载管理器
        /// 设置 HTTP 客户端默认配置
        /// </summary>
        public DownloadManager()
        {
            _httpClient = new HttpClient();

            // 设置超时时间为 30 分钟，适应大文件下载
            _httpClient.Timeout = TimeSpan.FromMinutes(30);

            // 设置 User-Agent，模拟浏览器请求
            // 某些服务器可能拒绝没有 User-Agent 的请求
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

            _downloadStopwatch = new Stopwatch();
            _lastUpdateTime = DateTime.Now;
        }

        #endregion

        #region 公共方法 - 控制

        /// <summary>
        /// 设置是否启用断点续传
        /// </summary>
        /// <param name="enable">true 启用，false 禁用</param>
        public void SetEnableResume(bool enable)
        {
            _enableResume = enable;
        }

        /// <summary>
        /// 取消当前下载
        /// </summary>
        public void CancelDownload()
        {
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// 切换暂停/恢复状态
        /// </summary>
        public void PauseResume()
        {
            if (!_isDownloading.Value)
                return;

            if (_pauseTokenSource.IsPaused)
            {
                Resume();
            }
            else
            {
                Pause();
            }
        }

        /// <summary>
        /// 暂停下载
        /// </summary>
        private void Pause()
        {
            _pauseTokenSource.Pause();
            _downloadStopwatch.Stop();
            Logger.Info("下载已暂停");
        }

        /// <summary>
        /// 恢复下载
        /// </summary>
        private void Resume()
        {
            _pauseTokenSource.Resume();
            _downloadStopwatch.Start();
            Logger.Info("下载已恢复");
        }

        #endregion

        #region 公共方法 - 下载

        /// <summary>
        /// 异步下载文件
        /// </summary>
        /// <param name="url">下载 URL</param>
        /// <param name="savePath">保存路径</param>
        /// <returns>下载是否成功</returns>
        /// <exception cref="InvalidOperationException">下载器已在使用中</exception>
        public async Task<bool> DownloadFileAsync(string url, string savePath)
        {
            // 检查是否已有下载任务
            if (_isDownloading.Value)
                return false;

            Logger.Info("开始下载: " + url);

            // 初始化取消令牌
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            // 重置状态
            _isDownloading.Value = true;
            _downloadStopwatch.Start();
            _lastBytesReceived = 0;
            _lastUpdateTime = DateTime.Now;
            _lastProgressUpdateTime = DateTime.MinValue;

            try
            {
                // 获取安全的文件名
                var fileName = GetSafeFileName(url);

                // 获取文件路径（处理重名）
                var (finalPath, partPath) = GetFilePaths(savePath, fileName);

                // 获取已下载大小（用于断点续传）
                long existingBytes = GetExistingFileSize(partPath);

                // 执行下载
                await DownloadInternalAsync(url, partPath, existingBytes, token);

                _downloadStopwatch.Stop();

                // 下载完成处理
                if (!token.IsCancellationRequested)
                {
                    // 移动临时文件到最终路径
                    if (File.Exists(partPath))
                    {
                        MoveFile(partPath, finalPath);
                    }

                    // 触发完成事件
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
                    // 取消完成
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
                // 操作取消
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
                // 下载错误
                Logger.Error("下载失败: " + ex.Message, ex);
                DownloadError?.Invoke(this, new DownloadErrorEventArgs { Exception = ex });
                return false;
            }
            finally
            {
                Cleanup();
            }
        }

        #endregion

        #region 私有方法 - 下载核心

        /// <summary>
        /// 内部下载实现
        /// </summary>
        /// <param name="url">下载 URL</param>
        /// <param name="filePath">临时文件路径</param>
        /// <param name="existingBytes">已下载字节数</param>
        /// <param name="cancellationToken">取消令牌</param>
        private async Task DownloadInternalAsync(
            string url,
            string filePath,
            long existingBytes,
            CancellationToken cancellationToken)
        {
            // 获取 HTTP 响应（带重试）
            using (var response = await GetResponseWithRetryAsync(url, existingBytes, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                // 计算总大小
                long totalBytes = response.Content.Headers.ContentLength ?? 0;
                long totalExpectedBytes = totalBytes > 0 ? existingBytes + totalBytes : 0;

                // 报告初始进度（续传时显示已下载部分）
                if (existingBytes > 0)
                {
                    OnProgressChanged(existingBytes, totalExpectedBytes);
                    _lastProgressUpdateTime = DateTime.Now;
                }

                _lastBytesReceived = existingBytes;
                _lastUpdateTime = DateTime.Now;

                // 确定文件打开模式
                var fileMode = (_enableResume && existingBytes > 0)
                    ? FileMode.Append    // 续传：追加模式
                    : FileMode.Create;   // 新下载：创建模式

                // 使用异步流下载
                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(
                    filePath,
                    fileMode,
                    FileAccess.Write,
                    FileShare.None,
                    DownloadBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[DownloadBufferSize];
                    long totalBytesRead = existingBytes;

                    // 下载循环
                    while (true)
                    {
                        // 检查取消请求
                        cancellationToken.ThrowIfCancellationRequested();

                        // 等待暂停解除
                        await _pauseTokenSource.Token.WaitWhilePausedAsync();

                        // 读取数据块
                        var bytesRead = await contentStream.ReadAsync(
                            buffer.AsMemory(0, buffer.Length), cancellationToken);

                        // 检查是否读取完毕
                        if (bytesRead == 0)
                            break;

                        // 写入文件
                        await fileStream.WriteAsync(
                            buffer.AsMemory(0, bytesRead), cancellationToken);

                        totalBytesRead += bytesRead;

                        // 更新进度（限制频率）
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

                    // 最终进度报告
                    if (!_pauseTokenSource.IsPaused)
                    {
                        OnProgressChanged(totalBytesRead, totalExpectedBytes);
                    }
                }
            }
        }

        /// <summary>
        /// 获取 HTTP 响应（带重试逻辑）
        /// </summary>
        /// <param name="url">请求 URL</param>
        /// <param name="resumeBytes">续传起始字节</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>HTTP 响应消息</returns>
        private async Task<HttpResponseMessage> GetResponseWithRetryAsync(
            string url,
            long resumeBytes,
            CancellationToken cancellationToken)
        {
            const int maxRetries = 3;
            int currentRetry = 0;
            long currentResumeBytes = resumeBytes;
            bool triedRange = currentResumeBytes > 0 && _enableResume;

            while (true)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);

                    // 设置 Range 头（断点续传）
                    if (_enableResume && currentResumeBytes > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(currentResumeBytes, null);
                    }

                    // 发送请求
                    var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                    // 处理续传失败回退
                    // 如果服务器不支持 Range 请求，返回 200 OK 或 416 错误
                    if (_enableResume && currentResumeBytes > 0 &&
                        (response.StatusCode == HttpStatusCode.OK ||
                         response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
                    {
                        response.Dispose();
                        Logger.Warn("服务端不支持续传，回退到整包下载");
                        currentResumeBytes = 0;

                        // 如果之前尝试过 Range，不再重试
                        if (triedRange)
                        {
                            triedRange = false;
                            continue;
                        }
                    }

                    return response;
                }
                catch (HttpRequestException) when (currentRetry < maxRetries)
                {
                    // 网络错误重试
                    currentRetry++;
                    await Task.Delay(TimeSpan.FromSeconds(currentRetry), cancellationToken);
                }
            }
        }

        #endregion

        #region 私有方法 - 辅助

        /// <summary>
        /// 触发进度变化事件
        /// </summary>
        /// <param name="bytesReceived">已接收字节数</param>
        /// <param name="totalBytes">总字节数</param>
        private void OnProgressChanged(long bytesReceived, long totalBytes)
        {
            var currentTime = DateTime.Now;
            var timeDiff = (currentTime - _lastUpdateTime).TotalSeconds;
            long speed = 0;

            // 计算下载速度（每秒至少 1 秒更新一次）
            if (timeDiff >= 1.0)
            {
                var bytesDiff = bytesReceived - _lastBytesReceived;
                speed = (long)(bytesDiff / timeDiff);
                _lastBytesReceived = bytesReceived;
                _lastUpdateTime = currentTime;
            }

            // 触发事件
            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
            {
                BytesReceived = bytesReceived,
                TotalBytes = totalBytes,
                Speed = speed
            });
        }

        /// <summary>
        /// 从 URL 提取安全的文件名
        /// </summary>
        /// <param name="url">下载 URL</param>
        /// <returns>安全的文件名</returns>
        private string GetSafeFileName(string url)
        {
            try
            {
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                if (!string.IsNullOrEmpty(fileName) && Path.HasExtension(fileName))
                {
                    return SanitizeFileName(fileName);
                }
            }
            catch { }

            // 无法从 URL 提取时，使用时间戳作为文件名
            return "download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }

        /// <summary>
        /// 清理文件名中的非法字符
        /// </summary>
        /// <param name="fileName">原始文件名</param>
        /// <returns>清理后的文件名</returns>
        private string SanitizeFileName(string fileName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }
            return fileName;
        }

        /// <summary>
        /// 获取文件路径（处理重名）
        /// </summary>
        /// <param name="savePath">保存目录</param>
        /// <param name="fileName">文件名</param>
        /// <returns>元组：(最终路径, 临时文件路径)</returns>
        private (string finalPath, string partPath) GetFilePaths(string savePath, string fileName)
        {
            var finalPath = Path.Combine(savePath, fileName);
            var partPath = finalPath + ".part";

            // 如果文件已存在，添加序号
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

        /// <summary>
        /// 获取已下载文件的大小
        /// </summary>
        /// <param name="partPath">临时文件路径</param>
        /// <returns>已下载字节数</returns>
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

        /// <summary>
        /// 移动文件（覆盖目标）
        /// </summary>
        /// <param name="sourcePath">源路径</param>
        /// <param name="destPath">目标路径</param>
        private void MoveFile(string sourcePath, string destPath)
        {
            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }
            File.Move(sourcePath, destPath);
        }

        /// <summary>
        /// 清理下载状态
        /// </summary>
        private void Cleanup()
        {
            _isDownloading.Value = false;
            _pauseTokenSource.Resume();
            _downloadStopwatch.Reset();

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        #endregion

        #region 静态方法 - ZIP 解压

        /// <summary>
        /// 异步解压 ZIP 文件（使用安全解压器）
        /// </summary>
        /// <param name="zipFilePath">ZIP 文件路径</param>
        /// <param name="extractPath">解压目标路径</param>
        /// <param name="progress">进度报告器</param>
        /// <param name="cancellationToken">取消令牌</param>
        public static async Task ExtractZipFileAsync(
            string zipFilePath,
            string extractPath,
            IProgress<ExtractionProgress> progress,
            CancellationToken cancellationToken)
        {
            // 使用 SafeZipExtractor 进行安全解压
            await SafeZipExtractor.ExtractAsync(zipFilePath, extractPath, progress, cancellationToken);
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            Cleanup();
            _httpClient?.Dispose();
        }

        #endregion
    }

    #region 事件参数类

    /// <summary>
    /// 下载进度事件参数
    /// </summary>
    public class DownloadProgressEventArgs : EventArgs
    {
        /// <summary>
        /// 已接收字节数
        /// </summary>
        public long BytesReceived { get; set; }

        /// <summary>
        /// 总字节数（未知时为 0）
        /// </summary>
        public long TotalBytes { get; set; }

        /// <summary>
        /// 当前下载速度（字节/秒）
        /// </summary>
        public long Speed { get; set; }
    }

    /// <summary>
    /// 下载完成事件参数
    /// </summary>
    public class DownloadCompletedEventArgs : EventArgs
    {
        /// <summary>
        /// 文件路径
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 是否被取消
        /// </summary>
        public bool Cancelled { get; set; }
    }

    /// <summary>
    /// 下载错误事件参数
    /// </summary>
    public class DownloadErrorEventArgs : EventArgs
    {
        /// <summary>
        /// 异常对象
        /// </summary>
        public Exception Exception { get; set; }
    }

    #endregion
}