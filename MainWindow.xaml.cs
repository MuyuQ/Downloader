using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox;
using WYDownloader.Core;

namespace WYDownloader
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int DownloadBufferSize = 131072;
        private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromMilliseconds(300);
        private HttpClient httpClient;
        private CancellationTokenSource cancellationTokenSource;
        private bool isDownloading = false;
        private bool isPaused = false;
        private Stopwatch downloadStopwatch;
        private long lastBytesReceived = 0;
        private DateTime lastUpdateTime;
        private DateTime lastProgressUpdateTime;
        private ConfigManager configManager;

        public MainWindow()
        {
            InitializeComponent();
            this.Title = "直链下载器";
            InitializeDownloader();
            LoadConfiguration();
            LoadBackgroundImageFromResource();

            // 添加窗口拖拽功能
            this.MouseLeftButtonDown += MainWindow_MouseLeftButtonDown;

            // 设置标题栏文本
            if (txtAnnouncementTitle != null)
            {
                txtAnnouncementTitle.Text = "直链下载器";
            }
        }

        private void MainWindow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void LoadConfiguration()
        {
            try
            {
                configManager = new ConfigManager();

                // 设置窗口大小
                this.Width = configManager.GetWindowWidth();
                this.Height = configManager.GetWindowHeight();

                // 加载公告内容
                txtAnnouncementTitle.Text = configManager.GetAnnouncementTitle();

                // 清空并重新加载公告内容
                txtAnnouncementContent.Children.Clear();
                string[] contentLines = configManager.GetAnnouncementContent();

                foreach (string line in contentLines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        var trimmedLine = line.Trim();

                        // 创建每行的容器
                        var linePanel = new StackPanel
                        {
                            Orientation = System.Windows.Controls.Orientation.Horizontal,
                            Margin = new Thickness(0, 0, 0, 12),
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
                        };

                        // 如果是以"•"开头的项目，添加特殊样式
                        if (trimmedLine.StartsWith("•"))
                        {
                            // 创建图标
                            var icon = new TextBlock
                            {
                                Text = "●",
                                FontSize = 12,
                                Foreground = new SolidColorBrush(Color.FromRgb(52, 152, 219)), // 蓝色圆点
                                Margin = new Thickness(0, 2, 10, 0),
                                VerticalAlignment = VerticalAlignment.Top
                            };
                            linePanel.Children.Add(icon);

                            // 创建文本（去掉原来的"•"）
                            var textBlock = new TextBlock
                            {
                                Text = trimmedLine.Substring(1).Trim(),
                                FontSize = 14,
                                Foreground = new SolidColorBrush(Color.FromRgb(236, 240, 241)),
                                TextWrapping = TextWrapping.Wrap,
                                LineHeight = 20,
                                MaxWidth = 400
                            };
                            linePanel.Children.Add(textBlock);
                        }
                        else
                        {
                            // 普通文本行
                            var textBlock = new TextBlock
                            {
                                Text = trimmedLine,
                                FontSize = 15,
                                Foreground = new SolidColorBrush(Color.FromRgb(245, 246, 250)),
                                TextWrapping = TextWrapping.Wrap,
                                LineHeight = 22,
                                FontWeight = FontWeights.Medium,
                                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                                TextAlignment = TextAlignment.Center
                            };
                            linePanel.Children.Add(textBlock);
                        }

                        txtAnnouncementContent.Children.Add(linePanel);
                    }
                }

                // 加载下载链接到下拉框
                LoadDownloadLinks();

                // 设置自动解压选项
                chkAutoExtract.IsChecked = configManager.GetAutoExtractZip();
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载配置文件失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadDownloadLinks()
        {
            try
            {
                var downloads = configManager.GetDownloadLinks();
                cmbDownloadLinks.Items.Clear();

                foreach (var download in downloads)
                {
                    cmbDownloadLinks.Items.Add(download.Key);
                }

                // 设置默认选择
                string defaultDownload = configManager.GetDefaultDownload();
                if (cmbDownloadLinks.Items.Contains(defaultDownload))
                {
                    cmbDownloadLinks.SelectedItem = defaultDownload;
                }
                else if (cmbDownloadLinks.Items.Count > 0)
                {
                    cmbDownloadLinks.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载下载链接失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CmbDownloadLinks_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cmbDownloadLinks.SelectedItem != null)
            {
                string selectedName = cmbDownloadLinks.SelectedItem.ToString();
                string url = configManager.GetDownloadUrl(selectedName);
                // 选择项目后更新进度显示
                lblProgress.Text = "已选择：" + selectedName;
            }
        }



        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                // 修改图标而不是替换 Content
                if (btnMaximize.Content is MaterialDesignThemes.Wpf.PackIcon icon)
                {
                    icon.Kind = MaterialDesignThemes.Wpf.PackIconKind.CheckboxMultipleBlankOutline;
                }
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                // 修改图标而不是替换 Content
                if (btnMaximize.Content is MaterialDesignThemes.Wpf.PackIcon icon)
                {
                    icon.Kind = MaterialDesignThemes.Wpf.PackIconKind.WindowRestore;
                }
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (isDownloading)
            {
                var result = MessageBox.Show("正在下载中，确定要退出吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No)
                {
                    return;
                }
                if (cancellationTokenSource != null)
                    cancellationTokenSource.Cancel();
            }
            this.Close();
        }

        private void BtnPauseResume_Click(object sender, RoutedEventArgs e)
        {
            if (!isDownloading)
                return;

            if (isPaused)
            {
                // 继续下载
                isPaused = false;
                SetPauseResumeIcon(false); // 显示暂停图标
                lblProgress.Text = "继续下载...";
                downloadStopwatch.Start();
            }
            else
            {
                // 暂停下载
                isPaused = true;
                SetPauseResumeIcon(true); // 显示播放图标
                lblProgress.Text = "下载已暂停";
                downloadStopwatch.Stop();
            }
        }

        private void SetPauseResumeIcon(bool showPlayIcon)
        {
            // 获取按钮模板中的图标元素
            var template = btnPauseResume.Template;
            if (template != null)
            {
                var icon = template.FindName("icon", btnPauseResume) as MaterialDesignThemes.Wpf.PackIcon;
                var border = template.FindName("border", btnPauseResume) as System.Windows.Controls.Border;
                
                if (icon != null)
                {
                    icon.Kind = showPlayIcon ? MaterialDesignThemes.Wpf.PackIconKind.Play : MaterialDesignThemes.Wpf.PackIconKind.Pause;
                }
                
                // 切换按钮颜色：播放（黄色）/暂停（蓝色）
                if (border != null)
                {
                    if (showPlayIcon)
                    {
                        // 显示播放图标时，使用黄色
                        border.Background = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(255, 193, 7));
                    }
                    else
                    {
                        // 显示暂停图标时，使用蓝色
                        border.Background = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(59, 130, 246));
                    }
                }
            }
        }



        private void InitializeDownloader()
        {
            // 下载路径固定为程序所在目录
            // txtSavePath.Text = AppDomain.CurrentDomain.BaseDirectory; // 已移除路径显示

            // 初始化HttpClient
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30); // 30分钟超时

            // 添加用户代理，避免某些网站拒绝请求
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

            downloadStopwatch = new Stopwatch();
            lastUpdateTime = DateTime.Now;

            // 设置窗口图标
            try
            {
                this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Resources/Icons/ico.ico"));
            }
            catch
            {
                // 忽略图标加载错误
            }
        }



        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[LOG] BtnDownload_Click 开始");
            
            if (isDownloading)
            {
                // 取消下载
                System.Diagnostics.Debug.WriteLine("[LOG] 取消下载");
                if (cancellationTokenSource != null)
                    cancellationTokenSource.Cancel();
                return;
            }

            // 检查是否选择了下载项目
            if (cmbDownloadLinks.SelectedItem == null)
            {
                System.Diagnostics.Debug.WriteLine("[LOG] 未选择下载项目");
                MessageBox.Show("请先选择要下载的项目！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedName = cmbDownloadLinks.SelectedItem.ToString();
            System.Diagnostics.Debug.WriteLine($"[LOG] 选择的项目：{selectedName}");
            
            var urls = configManager.ResolveDownloadUrls(selectedName, out string errorMessage);
            System.Diagnostics.Debug.WriteLine($"[LOG] 解析的 URL 数量：{urls.Count}");
            
            string savePath = configManager.GetDefaultDownloadPath(); // 使用配置的下载路径

            if (string.IsNullOrWhiteSpace(savePath))
            {
                savePath = AppDomain.CurrentDomain.BaseDirectory;
            }

            try
            {
                savePath = Path.GetFullPath(savePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] 下载路径无效：{ex.Message}");
                MessageBox.Show($"下载路径无效：{savePath}\n{ex.Message}",
                               "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[LOG] 保存路径：{savePath}");

            try
            {
                Directory.CreateDirectory(savePath);
                System.Diagnostics.Debug.WriteLine($"[LOG] 确认下载目录：{savePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] 创建目录失败：{ex.Message}");
                MessageBox.Show($"无法创建下载目录：{savePath}\n{ex.Message}",
                               "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 验证输入
            if (!string.IsNullOrEmpty(errorMessage))
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] 错误信息：{errorMessage}");
                MessageBox.Show(errorMessage, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var candidateUrls = new List<string>();
            foreach (var url in urls)
            {
                if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                {
                    continue;
                }

                Uri uri = new Uri(url);
                if (uri.Scheme != "http" && uri.Scheme != "https")
                {
                    continue;
                }

                candidateUrls.Add(url);
            }

            if (candidateUrls.Count == 0)
            {
                MessageBox.Show("所选项目\"" + selectedName + "\"的下载链接无效！\n请检查config.ini文件中的配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Exception lastException = null;
            for (int index = 0; index < candidateUrls.Count; index++)
            {
                var url = candidateUrls[index];
                bool hasMore = index < candidateUrls.Count - 1;
                string connectingMessage = candidateUrls.Count > 1
                    ? "正在连接... (" + (index + 1) + "/" + candidateUrls.Count + ")"
                    : "正在连接...";

                try
                {
                    await StartDownload(url, savePath, connectingMessage, hasMore);
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
                    {
                        ResetUI();
                        return;
                    }

                    if (hasMore)
                    {
                        lblProgress.Text = "当前地址失败，正在尝试备用地址...";
                        lblSpeed.Text = "";
                        progressBar.Value = 0;
                    }
                }
            }

            if (lastException != null)
            {
                MessageBox.Show("下载出错：" + lastException.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                ResetUI();
            }
        }

private async Task StartDownload(string url, string savePath, string connectingMessage, bool preserveUiOnFailure)
        {
            System.Diagnostics.Debug.WriteLine($"[LOG] StartDownload 开始 url={url}");
            
            cancellationTokenSource = new CancellationTokenSource();
            isDownloading = true;
            bool enableResume = configManager.GetEnableResume();
            System.Diagnostics.Debug.WriteLine($"[LOG] 断点续传配置：EnableResume={enableResume}");

            // 更新 UI 状态
            btnDownload.Content = "取消下载";
            btnDownload.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60));
            btnPauseResume.IsEnabled = true;
            SetPauseResumeIcon(false); // 显示暂停图标
            btnPauseResume.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0));
            progressBar.Value = 0;
            System.Diagnostics.Debug.WriteLine($"[LOG] progressBar.Value 设置为 0");
            lblProgress.Text = string.IsNullOrWhiteSpace(connectingMessage) ? "正在连接..." : connectingMessage;
            lblSpeed.Text = "";
            isPaused = false;

            bool shouldResetUi = true;
            try
            {
                // 获取文件名
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
                    System.Diagnostics.Debug.WriteLine($"[LOG] 启用断点续传，已存在 {existingBytes} 字节");
                }
                else if (!enableResume && File.Exists(partPath))
                {
                    // 禁用续传时删除旧的 part 文件
                    try
                    {
                        File.Delete(partPath);
                        System.Diagnostics.Debug.WriteLine($"[LOG] 禁用断点续传，删除旧的 part 文件：{partPath}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LOG] 删除 part 文件失败：{ex.Message}");
                    }
                }

                // 开始下载
                downloadStopwatch.Start();
                lastUpdateTime = DateTime.Now;
                lastProgressUpdateTime = DateTime.MinValue;
                lastBytesReceived = existingBytes;

                await DownloadFileAsync(url, partPath, existingBytes, enableResume, cancellationTokenSource.Token);

                downloadStopwatch.Stop();

                if (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    lblProgress.Text = "下载完成";

                    if (File.Exists(partPath))
                    {
                        if (File.Exists(finalPath))
                        {
                            File.Delete(finalPath);
                        }

                        File.Move(partPath, finalPath);
                    }

                    // 检查是否需要自动解压
                    if (chkAutoExtract.IsChecked == true && Path.GetExtension(finalPath).ToLower() == ".zip")
                    {
                        await ExtractZipFile(finalPath, savePath);
                    }

                    MessageBox.Show("下载完成！\n文件已保存到: " + finalPath, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (OperationCanceledException)
            {
                shouldResetUi = true;
                lblProgress.Text = enableResume
                    ? "下载已取消（已保留未完成文件，可继续下载）"
                    : "下载已取消";
            }
            catch (Exception ex)
            {
                shouldResetUi = !preserveUiOnFailure;
                lblProgress.Text = "下载失败，已保留未完成文件";
                throw;
            }
            finally
            {
                if (shouldResetUi)
                {
                    ResetUI();
                }
                else
                {
                    downloadStopwatch.Reset();
                }

                if (cancellationTokenSource != null)
                {
                    cancellationTokenSource.Dispose();
                    cancellationTokenSource = null;
                }
            }
        }

        private async Task DownloadFileAsync(string url, string filePath, long existingBytes, bool enableResume, CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine($"[LOG] DownloadFileAsync 开始 filePath={filePath}, existingBytes={existingBytes}");
            
            long resumeBytes = enableResume ? existingBytes : 0;
            bool triedRange = enableResume && resumeBytes > 0;
            HttpResponseMessage response = null;

            while (true)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (enableResume && resumeBytes > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(resumeBytes, null);
                }

                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (resumeBytes > 0 && (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
                {
                    response.Dispose();
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }

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
                System.Diagnostics.Debug.WriteLine($"[LOG] totalBytes={totalBytes}");
                
                long totalExpectedBytes = totalBytes > 0 ? resumeBytes + totalBytes : 0;
                System.Diagnostics.Debug.WriteLine($"[LOG] totalExpectedBytes={totalExpectedBytes}");
                
                long totalBytesRead = resumeBytes;

                if (resumeBytes > 0)
                {
                    await UpdateProgress(resumeBytes, totalExpectedBytes);
                    lastProgressUpdateTime = DateTime.Now;
                }

                lastBytesReceived = resumeBytes;
                lastUpdateTime = DateTime.Now;

                FileMode fileMode = resumeBytes > 0 ? FileMode.Append : FileMode.Create;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(filePath, fileMode, FileAccess.Write, FileShare.None, DownloadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[DownloadBufferSize];
                    var isMoreToRead = true;

                    do
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // 检查是否暂停
                        while (isPaused && !cancellationToken.IsCancellationRequested)
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
                        System.Diagnostics.Debug.WriteLine($"[LOG] 已读取 {totalBytesRead} 字节");

                        // 更新进度
                        if (!isPaused)
                        {
                            var now = DateTime.Now;
                            if (now - lastProgressUpdateTime >= ProgressUpdateInterval)
                            {
                                System.Diagnostics.Debug.WriteLine($"[LOG] 调用 UpdateProgress");
                                await UpdateProgress(totalBytesRead, totalExpectedBytes);
                                lastProgressUpdateTime = now;
                            }
                        }

                    } while (isMoreToRead);

                    if (!isPaused)
                    {
                        await UpdateProgress(totalBytesRead, totalExpectedBytes);
                    }
                }
            }
        }

private async Task UpdateProgress(long bytesReceived, long totalBytes)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] UpdateProgress: bytes={bytesReceived}, total={totalBytes}");
                
                if (totalBytes > 0)
                {
                    var progressPercentage = (double)bytesReceived / totalBytes * 100;
                    System.Diagnostics.Debug.WriteLine($"[LOG] Progress: {progressPercentage:F1}%, setting progressBar.Value={progressPercentage}");
                    System.Diagnostics.Debug.WriteLine($"[LOG] progressBar 当前值={progressBar.Value}, 最大值={progressBar.Maximum}");
                    
                    progressBar.Value = progressPercentage;
                    
                    System.Diagnostics.Debug.WriteLine($"[LOG] progressBar 新值={progressBar.Value}");
                    lblProgress.Text = FormatBytes(bytesReceived) + " / " + FormatBytes(totalBytes) + " (" + progressPercentage.ToString("F1") + "%)";
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[LOG] totalBytes=0, 不更新进度条");
                    lblProgress.Text = "已下载：" + FormatBytes(bytesReceived);
                }

                // 计算下载速度
                var currentTime = DateTime.Now;
                var timeDiff = (currentTime - lastUpdateTime).TotalSeconds;
                
                if (timeDiff >= 1.0) // 每秒更新一次速度
                {
                    var bytesDiff = bytesReceived - lastBytesReceived;
                    var speed = bytesDiff / timeDiff;
                    lblSpeed.Text = FormatBytes((long)speed) + "/s";
                    
                    lastBytesReceived = bytesReceived;
                    lastUpdateTime = currentTime;
                }
            });
        }

        private string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number = number / 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }

        private void ResetUI()
        {
            isDownloading = false;
            isPaused = false;
            btnDownload.Content = "开始下载";
            btnDownload.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 144, 226));
            btnPauseResume.IsEnabled = false;
            SetPauseResumeIcon(false); // 显示暂停图标
            btnPauseResume.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0));
            downloadStopwatch.Reset();
        }

        private async Task ExtractZipFile(string zipFilePath, string extractPath)
        {
            string extractDir = "";
            try
            {
                lblProgress.Text = "正在解压ZIP文件...";
                progressBar.Value = 0;

                await Task.Run(() =>
                {
                    // 创建解压目录
                    extractDir = Path.Combine(extractPath, Path.GetFileNameWithoutExtension(zipFilePath));
                    if (Directory.Exists(extractDir))
                    {
                        // 如果目录已存在，添加序号
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
                            if (cancellationTokenSource.Token.IsCancellationRequested)
                                break;

                            if (!string.IsNullOrEmpty(entry.Name))
                            {
                                string destinationPath = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));

                                if (!destinationPath.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase))
                                {
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
                            }

                            processedEntries++;
                            var progress = (double)processedEntries / totalEntries * 100;

                            Dispatcher.Invoke(() =>
                            {
                                progressBar.Value = progress;
                                lblProgress.Text = "解压中... (" + processedEntries + "/" + totalEntries + ")";
                            });
                        }
                    }

                    Dispatcher.Invoke(() =>
                    {
                        if (!cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            lblProgress.Text = "解压完成";
                            progressBar.Value = 100;
                        }
                    });
                });

                if (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    MessageBox.Show("ZIP文件解压完成！\n文件已解压到: " + extractDir, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("解压失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                lblProgress.Text = "解压失败";
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (isDownloading)
            {
                var result = MessageBox.Show("正在下载中，确定要退出吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
                if (cancellationTokenSource != null)
                    cancellationTokenSource.Cancel();
            }

            if (httpClient != null)
                httpClient.Dispose();
            base.OnClosing(e);
        }

        /// <summary>
        /// 优先从本地加载背景图片，支持多背景轮换，失败回退到嵌入资源
        /// </summary>
        private void LoadBackgroundImageFromResource()
        {
            try
            {
                // 首先检查配置中是否有自定义背景图片列表
                var backgroundImages = configManager.GetBackgroundImages();
                
                if (backgroundImages.Count > 0)
                {
                    // 随机选择一张背景图片
                    var random = new Random();
                    var selectedImage = backgroundImages[random.Next(backgroundImages.Count)];
                    
                    // 尝试从多个位置加载
                    string[] possiblePaths = new string[]
                    {
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Images", "background", selectedImage),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, selectedImage),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Images", selectedImage)
                    };
                    
                    foreach (var imagePath in possiblePaths)
                    {
                        if (File.Exists(imagePath))
                        {
                            LoadImageFromFile(imagePath);
                            return;
                        }
                    }
                }
                
                // 尝试加载默认背景图片
                string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Images", "BG.JPG");
                if (File.Exists(localPath))
                {
                    LoadImageFromFile(localPath);
                    return;
                }

                // 从嵌入资源加载
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("WYDownloader.Resources.Images.BG.JPG"))
                {
                    if (stream != null)
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = stream;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        BackgroundImage.Source = bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                // 如果加载失败，使用默认背景色
                System.Diagnostics.Debug.WriteLine("加载背景图片失败: " + ex.Message);
            }
        }
        
        private void LoadImageFromFile(string filePath)
        {
            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = fileStream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                BackgroundImage.Source = bitmap;
            }
        }
    }
}
