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
        
        public bool IsDownloading { get; private set; } = false;
        public bool IsPaused { get; private set; } = false;
        
        public event EventHandler<DownloadProgressEventArgs> ProgressChanged;
        public event EventHandler<DownloadCompletedEventArgs> DownloadCompleted;
        public event EventHandler<DownloadErrorEventArgs> DownloadError;
        
        private bool enableResume = true;

        public DownloadManager()
        {
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            
            downloadStopwatch = new Stopwatch();
            lastUpdateTime = DateTime.Now;
        }

        public void SetEnableResume(bool enable)
        {
            enableResume = enable;
        }

        public void CancelDownload()
        {
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Cancel();
            }
        }

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

        public async Task<bool> DownloadFileAsync(string url, string savePath)
        {
            if (IsDownloading)
                return false;

            Logger.Info("开始下载: " + url);

            cancellationTokenSource = new CancellationTokenSource();
            IsDownloading = true;
            IsPaused = false;
            downloadStopwatch.Start();
            lastUpdateTime = DateTime.Now;
            lastProgressUpdateTime = DateTime.MinValue;
            lastBytesReceived = 0;

            try
            {
                string fileName = Path.GetFileName(new Uri(url).LocalPath);
                if (string.IsNullOrEmpty(fileName) || !Path.HasExtension(fileName))
                {
                    fileName = "download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                }

                string finalPath = Path.Combine(savePath, fileName);
                string partPath = finalPath + ".part";

                if (File.Exists(finalPath))
                {
                    int counter = 1;
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(finalPath);
                    string extension = Path.GetExtension(finalPath);

                    do
                    {
                        finalPath = Path.Combine(savePath, nameWithoutExt + "_" + counter + extension);
                        partPath = finalPath + ".part";
                        counter++;
                    } while (File.Exists(finalPath) || File.Exists(partPath));
                }

                long existingBytes = 0;
                if (enableResume && File.Exists(partPath))
                {
                    existingBytes = new FileInfo(partPath).Length;
                    Logger.Info("检测到断点文件，尝试续传，已下载字节: " + existingBytes);
                }
                else if (!enableResume && File.Exists(partPath))
                {
                    try
                    {
                        File.Delete(partPath);
                        Logger.Info("已禁用续传，删除旧的 .part 文件");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("删除旧 .part 文件失败: " + ex.Message);
                    }
                }

                await DownloadFileInternalAsync(url, partPath, existingBytes, cancellationTokenSource.Token);

                downloadStopwatch.Stop();

                if (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    if (File.Exists(partPath))
                    {
                        if (File.Exists(finalPath))
                        {
                            File.Delete(finalPath);
                        }

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
                DownloadError?.Invoke(this, new DownloadErrorEventArgs
                {
                    Exception = ex
                });
                return false;
            }
            finally
            {
                IsDownloading = false;
                IsPaused = false;
                downloadStopwatch.Reset();
                
                if (cancellationTokenSource != null)
                {
                    cancellationTokenSource.Dispose();
                    cancellationTokenSource = null;
                }
            }
        }

        private async Task DownloadFileInternalAsync(string url, string filePath, long existingBytes, CancellationToken cancellationToken)
        {
            long resumeBytes = existingBytes;
            bool triedRange = resumeBytes > 0 && enableResume;
            HttpResponseMessage response = null;

            while (true)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (enableResume && resumeBytes > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(resumeBytes, null);
                    }

                    response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }

                if (enableResume && resumeBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var contentRangeFrom = response.Content.Headers.ContentRange?.From;
                    if (!contentRangeFrom.HasValue || contentRangeFrom.Value != resumeBytes)
                    {
                        response.Dispose();
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                        }

                        Logger.Warn("Content-Range 校验失败，已清除断点文件并回退整包下载");

                        resumeBytes = 0;
                        if (triedRange)
                        {
                            triedRange = false;
                            continue;
                        }
                    }
                }

                if (enableResume && resumeBytes > 0 && (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
                {
                    response.Dispose();
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }

                    Logger.Warn("服务端不接受续传请求，已回退整包下载");

                    resumeBytes = 0;
                    if (triedRange)
                    {
                        triedRange = false;
                        continue;
                    }
                }

                break;
            }

            using (response)
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

                FileMode fileMode = (enableResume && resumeBytes > 0) ? FileMode.Append : FileMode.Create;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(filePath, fileMode, FileAccess.Write, FileShare.None, DownloadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
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

                        var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
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

        public static async Task ExtractZipFileAsync(string zipFilePath, string extractPath, IProgress<ExtractionProgress> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                string extractDir = Path.Combine(extractPath, Path.GetFileNameWithoutExtension(zipFilePath));
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

                using (var archive = ZipFile.OpenRead(zipFilePath))
                {
                    int totalEntries = archive.Entries.Count;
                    int processedEntries = 0;
                    string extractRoot = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;

                    foreach (var entry in archive.Entries)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        if (!string.IsNullOrEmpty(entry.Name))
                        {
                            string destinationPath = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));

                            if (!destinationPath.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string destinationDir = Path.GetDirectoryName(destinationPath);
                            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                            {
                                Directory.CreateDirectory(destinationDir);
                            }

                            entry.ExtractToFile(destinationPath, true);
                        }

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
            if (httpClient != null)
            {
                httpClient.Dispose();
                httpClient = null;
            }
            
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Dispose();
                cancellationTokenSource = null;
            }
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
