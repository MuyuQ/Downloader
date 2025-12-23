using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox;

namespace WYDownloader
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private HttpClient httpClient;
        private CancellationTokenSource cancellationTokenSource;
        private bool isDownloading = false;
        private bool isPaused = false;
        private Stopwatch downloadStopwatch;
        private long lastBytesReceived = 0;
        private DateTime lastUpdateTime;
        private ConfigManager configManager;
        private string currentDownloadFilePath = "";
        private Stream currentDownloadStream;
        private FileStream currentFileStream;

        public MainWindow()
        {
            InitializeComponent();
            InitializeDownloader();
            LoadConfiguration();
            LoadBackgroundImageFromResource();

            // 添加窗口拖拽功能
            this.MouseLeftButtonDown += MainWindow_MouseLeftButtonDown;
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
                btnMaximize.Content = "□";
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                btnMaximize.Content = "❐";
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
                btnPauseResume.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0)); // #FF9800
                lblProgress.Text = "继续下载...";
                downloadStopwatch.Start();
            }
            else
            {
                // 暂停下载
                isPaused = true;
                SetPauseResumeIcon(true); // 显示播放图标
                btnPauseResume.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // #4CAF50
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
                var pauseIcon = template.FindName("PauseIcon", btnPauseResume) as System.Windows.Shapes.Path;
                var playIcon = template.FindName("PlayIcon", btnPauseResume) as System.Windows.Shapes.Path;

                if (pauseIcon != null && playIcon != null)
                {
                    if (showPlayIcon)
                    {
                        pauseIcon.Visibility = Visibility.Collapsed;
                        playIcon.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        pauseIcon.Visibility = Visibility.Visible;
                        playIcon.Visibility = Visibility.Collapsed;
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
                this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/ico.ico"));
            }
            catch
            {
                // 忽略图标加载错误
            }
        }



        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (isDownloading)
            {
                // 取消下载
                if (cancellationTokenSource != null)
                    cancellationTokenSource.Cancel();
                return;
            }

            // 检查是否选择了下载项目
            if (cmbDownloadLinks.SelectedItem == null)
            {
                MessageBox.Show("请先选择要下载的项目！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedName = cmbDownloadLinks.SelectedItem.ToString();
            string url = configManager.GetDownloadUrl(selectedName);
            string savePath = AppDomain.CurrentDomain.BaseDirectory; // 固定使用程序当前目录

            // 验证输入
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("所选项目\"" + selectedName + "\"的下载链接为空！\n请检查config.ini文件中的配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                MessageBox.Show("所选项目\"" + selectedName + "\"的下载链接无效！\n请检查config.ini文件中的配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 检查URL协议
            Uri uri = new Uri(url);
            if (uri.Scheme != "http" && uri.Scheme != "https")
            {
                MessageBox.Show("仅支持HTTP和HTTPS协议的链接！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                await StartDownload(url, savePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("下载出错：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                ResetUI();
            }
        }

        private async Task StartDownload(string url, string savePath)
        {
            cancellationTokenSource = new CancellationTokenSource();
            isDownloading = true;

            // 更新UI状态
            btnDownload.Content = "取消下载";
            btnDownload.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60));
            btnPauseResume.IsEnabled = true;
            SetPauseResumeIcon(false); // 显示暂停图标
            btnPauseResume.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0));
            progressBar.Value = 0;
            lblProgress.Text = "正在连接...";
            lblSpeed.Text = "";
            isPaused = false;

            try
            {
                // 获取文件名
                string fileName = Path.GetFileName(new Uri(url).LocalPath);
                if (string.IsNullOrEmpty(fileName) || !Path.HasExtension(fileName))
                {
                    fileName = "download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                }

                string fullPath = Path.Combine(savePath, fileName);

                // 如果文件已存在，添加序号
                int counter = 1;
                string originalPath = fullPath;
                while (File.Exists(fullPath))
                {
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(originalPath);
                    string extension = Path.GetExtension(originalPath);
                    fullPath = Path.Combine(savePath, nameWithoutExt + "_" + counter + extension);
                    counter++;
                }

                // 记录当前下载的文件路径
                currentDownloadFilePath = fullPath;

                // 开始下载
                downloadStopwatch.Start();
                lastUpdateTime = DateTime.Now;
                lastBytesReceived = 0;

                await DownloadFileAsync(url, fullPath, cancellationTokenSource.Token);

                downloadStopwatch.Stop();

                if (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    lblProgress.Text = "下载完成";
                    currentDownloadFilePath = ""; // 下载完成，清空路径

                    // 检查是否需要自动解压
                    if (chkAutoExtract.IsChecked == true && Path.GetExtension(fullPath).ToLower() == ".zip")
                    {
                        await ExtractZipFile(fullPath, savePath);
                    }

                    MessageBox.Show("下载完成！\n文件已保存到: " + fullPath, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (OperationCanceledException)
            {
                lblProgress.Text = "下载已取消";

                // 删除未完成的文件
                try
                {
                    if (!string.IsNullOrEmpty(currentDownloadFilePath) && File.Exists(currentDownloadFilePath))
                    {
                        File.Delete(currentDownloadFilePath);
                        lblProgress.Text = "下载已取消，未完成文件已删除";
                    }
                }
                catch (Exception deleteEx)
                {
                    // 如果删除失败，记录但不影响主流程
                    lblProgress.Text = "下载已取消，但删除文件失败：" + deleteEx.Message;
                }
            }
            catch (Exception ex)
            {
                lblProgress.Text = "下载失败";

                // 下载失败时也删除未完成的文件
                try
                {
                    if (!string.IsNullOrEmpty(currentDownloadFilePath) && File.Exists(currentDownloadFilePath))
                    {
                        File.Delete(currentDownloadFilePath);
                    }
                }
                catch
                {
                    // 忽略删除失败的错误
                }

                throw;
            }
            finally
            {
                currentDownloadFilePath = ""; // 清空文件路径
                ResetUI();
            }
        }

        private async Task DownloadFileAsync(string url, string filePath, CancellationToken cancellationToken)
        {
            using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                var totalBytesRead = 0L;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    currentDownloadStream = contentStream;
                    currentFileStream = fileStream;

                    var buffer = new byte[8192];
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

                        // 更新进度
                        if (!isPaused)
                        {
                            await UpdateProgress(totalBytesRead, totalBytes);
                        }

                    } while (isMoreToRead);

                    currentDownloadStream = null;
                    currentFileStream = null;
                }
            }
        }

        private async Task UpdateProgress(long bytesReceived, long totalBytes)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (totalBytes > 0)
                {
                    var progressPercentage = (double)bytesReceived / totalBytes * 100;
                    progressBar.Value = progressPercentage;
                    lblProgress.Text = FormatBytes(bytesReceived) + " / " + FormatBytes(totalBytes) + " (" + progressPercentage.ToString("F1") + "%)";
                }
                else
                {
                    lblProgress.Text = "已下载: " + FormatBytes(bytesReceived);
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

                        foreach (var entry in archive.Entries)
                        {
                            if (cancellationTokenSource.Token.IsCancellationRequested)
                                break;

                            if (!string.IsNullOrEmpty(entry.Name))
                            {
                                string destinationPath = Path.Combine(extractDir, entry.FullName);

                                // 确保目录存在
                                string destinationDir = Path.GetDirectoryName(destinationPath);
                                if (!Directory.Exists(destinationDir))
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
        /// 从嵌入资源加载背景图片
        /// </summary>
        private void LoadBackgroundImageFromResource()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("WYDownloader.BG.JPG"))
                {
                    if (stream != null)
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = stream;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        BackgroundImageBrush.ImageSource = bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                // 如果加载失败，使用默认背景色
                System.Diagnostics.Debug.WriteLine("加载背景图片失败: " + ex.Message);
            }
        }
    }
}
