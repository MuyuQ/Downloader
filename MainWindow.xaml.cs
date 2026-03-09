using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DownloadProgressEventArgs = WYDownloader.Core.DownloadProgressEventArgs;
using MessageBox = System.Windows.MessageBox;
using WYDownloader.Core;

namespace WYDownloader
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool isDownloading = false;
        private bool isPaused = false;
        private Stopwatch downloadStopwatch;
        private ConfigManager configManager;
        private DownloadManager downloadManager;
        private Task activeDownloadTask;
        private bool isClosingInProgress = false;
        private bool closeAfterCancelReady = false;
        private bool cancelRequested = false;
        private bool IsShuttingDown => isClosingInProgress || closeAfterCancelReady;

        public MainWindow()
        {
            InitializeComponent();
            this.Title = "直链下载器";
            Logger.Info("应用启动");
            InitializeDownloader();
            LoadConfiguration();
            LoadBackgroundImageFromResource();

            // 设置标题栏文本
            if (txtAnnouncementTitle != null)
            {
                txtAnnouncementTitle.Text = "直链下载器";
            }

        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
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
                                FontSize = 10,
                                Foreground = new SolidColorBrush(Color.FromRgb(52, 152, 219)), // 蓝色圆点
                                Margin = new Thickness(0, 1, 8, 0),
                                VerticalAlignment = VerticalAlignment.Top
                            };
                            linePanel.Children.Add(icon);

                            // 创建文本（去掉原来的"•"）
                            var textBlock = new TextBlock
                            {
                                Text = trimmedLine.Substring(1).Trim(),
                                FontSize = 12,
                                Foreground = new SolidColorBrush(Color.FromRgb(236, 240, 241)),
                                TextWrapping = TextWrapping.Wrap,
                                LineHeight = 18,
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
                                FontSize = 13,
                                Foreground = new SolidColorBrush(Color.FromRgb(245, 246, 250)),
                                TextWrapping = TextWrapping.Wrap,
                                LineHeight = 20,
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
                // 选择项目后更新进度显示
                lblProgress.Text = "已选择：" + selectedName;
            }
        }

        private void CmbDownloadLinks_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!(sender is ComboBox comboBox))
            {
                return;
            }

            if (!comboBox.IsDropDownOpen)
            {
                comboBox.Focus();
                comboBox.IsDropDownOpen = true;
                e.Handled = true;
            }
        }



        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void BtnPauseResume_Click(object sender, RoutedEventArgs e)
        {
            if (!isDownloading || downloadManager == null)
                return;

            downloadManager.PauseResume();
            isPaused = downloadManager.IsPaused;

            if (isPaused)
            {
                SetPauseResumeIcon(true); // 显示播放图标
                lblProgress.Text = "下载已暂停";
                downloadStopwatch.Stop();
                return;
            }

            SetPauseResumeIcon(false); // 显示暂停图标
            lblProgress.Text = "继续下载...";
            downloadStopwatch.Start();
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
            downloadManager = new DownloadManager();

            downloadStopwatch = new Stopwatch();

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
            Logger.Debug("[LOG] BtnDownload_Click 开始");
            
            if (isDownloading)
            {
                // 取消下载
                Logger.Debug("[LOG] 取消下载");
                cancelRequested = true;
                downloadManager?.CancelDownload();
                lblProgress.Text = "正在取消...";
                return;
            }

            // 检查是否选择了下载项目
            if (cmbDownloadLinks.SelectedItem == null)
            {
                Logger.Debug("[LOG] 未选择下载项目");
                MessageBox.Show("请先选择要下载的项目！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string selectedName = cmbDownloadLinks.SelectedItem.ToString();
            Logger.Debug($"[LOG] 选择的项目：{selectedName}");
            
            var urls = configManager.ResolveDownloadUrls(selectedName, out string errorMessage);
            Logger.Debug($"[LOG] 解析的 URL 数量：{urls.Count}");
            
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
                Logger.Error($"[LOG] 下载路径无效：{ex.Message}", ex);
                MessageBox.Show($"下载路径无效：{savePath}\n{ex.Message}",
                               "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Logger.Debug($"[LOG] 保存路径：{savePath}");

            try
            {
                Directory.CreateDirectory(savePath);
                Logger.Info($"[LOG] 确认下载目录：{savePath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[LOG] 创建目录失败：{ex.Message}", ex);
                MessageBox.Show($"无法创建下载目录：{savePath}\n{ex.Message}",
                               "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 验证输入
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Logger.Debug($"[LOG] 错误信息：{errorMessage}");
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
                    activeDownloadTask = StartDownload(url, savePath, connectingMessage, hasMore);
                    await activeDownloadTask;
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (ex is OperationCanceledException || IsShuttingDown)
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
                finally
                {
                    activeDownloadTask = null;
                }
            }

            if (lastException != null)
            {
                if (!IsShuttingDown)
                {
                    MessageBox.Show("下载出错：" + lastException.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                ResetUI();
            }
        }

        private async Task StartDownload(string url, string savePath, string connectingMessage, bool preserveUiOnFailure)
        {
            Logger.Debug($"[LOG] StartDownload 开始 url={url}");

            if (downloadManager == null)
            {
                throw new InvalidOperationException("下载器未初始化");
            }

            cancelRequested = false;
            isDownloading = true;
            bool enableResume = configManager.GetEnableResume();
            Logger.Debug($"[LOG] 断点续传配置：EnableResume={enableResume}");
            downloadManager.SetEnableResume(enableResume);

            // 更新 UI 状态
            btnDownload.Content = "取消下载";
            btnDownload.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60));
            btnPauseResume.Visibility = Visibility.Visible;
            btnPauseResume.IsEnabled = true;
            SetPauseResumeIcon(false); // 显示暂停图标
            btnPauseResume.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0));
            progressBar.Value = 0;
            lblProgress.Text = string.IsNullOrWhiteSpace(connectingMessage) ? "正在连接..." : connectingMessage;
            lblSpeed.Text = "";
            isPaused = false;

            DownloadCompletedEventArgs completionArgs = null;
            Exception downloadError = null;
            EventHandler<DownloadProgressEventArgs> progressHandler = (s, e) => Dispatcher.InvokeAsync(() =>
            {
                if (e.TotalBytes > 0)
                {
                    var progressPercentage = (double)e.BytesReceived / e.TotalBytes * 100;
                    progressBar.Value = progressPercentage;
                    lblProgress.Text = FormatBytes(e.BytesReceived) + " / " + FormatBytes(e.TotalBytes) + " (" + progressPercentage.ToString("F1") + "%)";
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
            EventHandler<DownloadCompletedEventArgs> completedHandler = (s, e) => completionArgs = e;
            EventHandler<DownloadErrorEventArgs> errorHandler = (s, e) => downloadError = e.Exception;

            downloadManager.ProgressChanged += progressHandler;
            downloadManager.DownloadCompleted += completedHandler;
            downloadManager.DownloadError += errorHandler;

            bool shouldResetUi = true;
            try
            {
                downloadStopwatch.Start();
                bool success = await downloadManager.DownloadFileAsync(url, savePath);
                downloadStopwatch.Stop();

                if (completionArgs != null && completionArgs.Cancelled)
                {
                    throw new OperationCanceledException();
                }

                if (!success)
                {
                    if (downloadError != null)
                    {
                        throw downloadError;
                    }

                    throw new InvalidOperationException("下载失败");
                }

                string finalPath = completionArgs?.FilePath;
                if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
                {
                    throw new IOException("下载完成但未找到输出文件");
                }

                lblProgress.Text = "下载完成";
                progressBar.Value = 100;

                // 检查是否需要自动解压
                if (chkAutoExtract.IsChecked == true && Path.GetExtension(finalPath).ToLower() == ".zip")
                {
                    await ExtractZipFile(finalPath, savePath);
                }

                if (cancelRequested)
                {
                    throw new OperationCanceledException();
                }

                if (!IsShuttingDown)
                {
                    MessageBox.Show("下载完成！\n文件已保存到: " + finalPath, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (OperationCanceledException)
            {
                shouldResetUi = true;
                lblProgress.Text = cancelRequested && !enableResume
                    ? "操作已取消"
                    : (enableResume ? "下载已取消（已保留未完成文件，可继续下载）" : "下载已取消");
            }
            catch (Exception)
            {
                shouldResetUi = !preserveUiOnFailure;
                lblProgress.Text = "下载失败，已保留未完成文件";
                throw;
            }
            finally
            {
                downloadManager.ProgressChanged -= progressHandler;
                downloadManager.DownloadCompleted -= completedHandler;
                downloadManager.DownloadError -= errorHandler;

                if (shouldResetUi)
                {
                    ResetUI();
                }
                else
                {
                    downloadStopwatch.Reset();
                }
            }
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
            cancelRequested = false;
            btnDownload.Content = "开始下载";
            btnDownload.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 144, 226));
            btnPauseResume.IsEnabled = false;
            btnPauseResume.Visibility = Visibility.Collapsed;
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
                        DateTime lastUiUpdateTime = DateTime.MinValue;

                        if (totalEntries == 0)
                        {
                            Dispatcher.InvokeAsync(() =>
                            {
                                lblProgress.Text = "解压完成（空压缩包）";
                                progressBar.Value = 100;
                            });
                            return;
                        }

                        foreach (var entry in archive.Entries)
                        {
                            if (IsShuttingDown || cancelRequested)
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
                            var now = DateTime.Now;
                            if ((now - lastUiUpdateTime).TotalMilliseconds >= 100 || processedEntries == totalEntries)
                            {
                                lastUiUpdateTime = now;
                                Dispatcher.InvokeAsync(() =>
                                {
                                    progressBar.Value = progress;
                                    lblProgress.Text = "解压中... (" + processedEntries + "/" + totalEntries + ")";
                                });
                            }
                        }
                    }

                    Dispatcher.Invoke(() =>
                    {
                        if (!IsShuttingDown && !cancelRequested)
                        {
                            lblProgress.Text = "解压完成";
                            progressBar.Value = 100;
                        }
                    });
                });

                if (cancelRequested)
                {
                    throw new OperationCanceledException();
                }

                if (!IsShuttingDown)
                {
                    MessageBox.Show("ZIP文件解压完成！\n文件已解压到: " + extractDir, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (OperationCanceledException)
            {
                lblProgress.Text = "解压已取消";
                throw;
            }
            catch (Exception ex)
            {
                if (!IsShuttingDown)
                {
                    MessageBox.Show("解压失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                lblProgress.Text = "解压失败";
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (closeAfterCancelReady)
            {
                if (downloadManager != null)
                {
                    downloadManager.Dispose();
                    downloadManager = null;
                }

                base.OnClosing(e);
                return;
            }

            if (isDownloading)
            {
                if (isClosingInProgress)
                {
                    e.Cancel = true;
                    return;
                }

                var result = MessageBox.Show("正在下载中，确定要退出吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }

                e.Cancel = true;
                if (!isClosingInProgress)
                {
                    isClosingInProgress = true;
                    BeginCloseAfterCancelAsync();
                }

                return;
            }

            if (downloadManager != null)
            {
                downloadManager.Dispose();
                downloadManager = null;
            }

            base.OnClosing(e);
        }

        private async void BeginCloseAfterCancelAsync()
        {
            try
            {
                cancelRequested = true;
                downloadManager?.CancelDownload();

                if (activeDownloadTask != null)
                {
                    await activeDownloadTask;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("关闭流程等待下载任务结束时发生异常: " + ex.Message);
            }
            finally
            {
                closeAfterCancelReady = true;
                isClosingInProgress = false;
                Dispatcher.Invoke(Close);
            }
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
                Logger.Error("加载背景图片失败", ex);
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
