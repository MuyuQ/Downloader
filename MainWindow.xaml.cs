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
using WYDownloader.Core.Security;

namespace WYDownloader
{
    /// <summary>
    /// 主窗口类
    /// 负责应用程序的用户界面交互和下载流程控制
    /// </summary>
    /// <remarks>
    /// 主要功能：
    /// - 显示公告内容
    /// - 选择和管理下载项目
    /// - 控制下载流程（开始、暂停、取消）
    /// - 显示下载进度和速度
    /// - 自动解压 ZIP 文件
    /// </remarks>
    public partial class MainWindow : Window
    {
        #region 私有字段 - 下载状态

        /// <summary>
        /// 是否正在下载
        /// </summary>
        private bool isDownloading = false;

        /// <summary>
        /// 是否已暂停
        /// </summary>
        private bool isPaused = false;

        /// <summary>
        /// 下载计时器
        /// 用于计算下载时间
        /// </summary>
        private Stopwatch downloadStopwatch;

        #endregion

        #region 私有字段 - 组件

        /// <summary>
        /// 配置管理器实例
        /// </summary>
        private ConfigManager configManager;

        /// <summary>
        /// 下载管理器实例
        /// </summary>
        private DownloadManager downloadManager;

        /// <summary>
        /// 当前活动的下载任务
        /// </summary>
        private Task activeDownloadTask;

        #endregion

        #region 私有字段 - 关闭状态

        /// <summary>
        /// 是否正在关闭中
        /// </summary>
        private bool isClosingInProgress = false;

        /// <summary>
        /// 取消后是否准备好关闭
        /// </summary>
        private bool closeAfterCancelReady = false;

        /// <summary>
        /// 是否请求取消下载
        /// </summary>
        private bool cancelRequested = false;

        /// <summary>
        /// 是否正在关闭流程中
        /// </summary>
        private bool IsShuttingDown => isClosingInProgress || closeAfterCancelReady;

        #endregion

        #region 私有字段 - 事件状态存储

        /// <summary>
        /// 最后的下载完成事件参数
        /// 用于在异步操作中传递事件结果
        /// </summary>
        private DownloadCompletedEventArgs _lastCompletedArgs;

        /// <summary>
        /// 最后的下载错误异常
        /// 用于在异步操作中传递错误信息
        /// </summary>
        private Exception _lastDownloadError;

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化主窗口
        /// </summary>
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

        #endregion

        #region 窗口事件处理

        /// <summary>
        /// 标题栏鼠标左键按下事件
        /// 实现窗口拖动功能
        /// </summary>
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        #endregion

        #region 配置加载

        /// <summary>
        /// 加载应用程序配置
        /// 包括窗口大小、公告内容、下载链接等
        /// </summary>
        private void LoadConfiguration()
        {
            try
            {
                configManager = new ConfigManager();

                // 设置窗口大小
                this.Width = configManager.GetWindowWidth();
                this.Height = configManager.GetWindowHeight();

                // 加载公告标题
                txtAnnouncementTitle.Text = configManager.GetAnnouncementTitle();

                // 清空并重新加载公告内容
                txtAnnouncementContent.Children.Clear();
                string[] contentLines = configManager.GetAnnouncementContent();

                foreach (string line in contentLines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        var trimmedLine = line.Trim();
                        CreateAnnouncementLine(trimmedLine);
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

        /// <summary>
        /// 创建公告内容行
        /// </summary>
        /// <param name="line">公告文本行</param>
        private void CreateAnnouncementLine(string line)
        {
            // 创建每行的容器
            var linePanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };

            // 如果是以"•"开头的项目，添加特殊样式
            if (line.StartsWith("•"))
            {
                // 创建蓝色圆点图标
                var icon = new TextBlock
                {
                    Text = "●",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(52, 152, 219)),
                    Margin = new Thickness(0, 1, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                linePanel.Children.Add(icon);

                // 创建文本（去掉原来的"•"）
                var textBlock = new TextBlock
                {
                    Text = line.Substring(1).Trim(),
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
                // 普通文本行（居中显示）
                var textBlock = new TextBlock
                {
                    Text = line,
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

        /// <summary>
        /// 加载下载链接到下拉框
        /// </summary>
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

        #endregion

        #region 下拉框事件处理

        /// <summary>
        /// 下载项目选择变化事件
        /// </summary>
        private void CmbDownloadLinks_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cmbDownloadLinks.SelectedItem != null)
            {
                string selectedName = cmbDownloadLinks.SelectedItem.ToString();
                lblProgress.Text = "已选择：" + selectedName;
            }
        }

        /// <summary>
        /// 下拉框预览鼠标左键按下事件
        /// 实现点击打开下拉框功能
        /// </summary>
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

        #endregion

        #region 窗口控制按钮

        /// <summary>
        /// 最小化按钮点击事件
        /// </summary>
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        /// <summary>
        /// 关闭按钮点击事件
        /// </summary>
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        #endregion

        #region 下载控制

        /// <summary>
        /// 暂停/继续按钮点击事件
        /// </summary>
        private void BtnPauseResume_Click(object sender, RoutedEventArgs e)
        {
            if (!isDownloading || downloadManager == null)
                return;

            // 切换暂停状态
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

        /// <summary>
        /// 设置暂停/继续按钮图标
        /// </summary>
        /// <param name="showPlayIcon">true 显示播放图标，false 显示暂停图标</param>
        private void SetPauseResumeIcon(bool showPlayIcon)
        {
            var template = btnPauseResume.Template;
            if (template != null)
            {
                // 获取图标元素
                var icon = template.FindName("icon", btnPauseResume) as MaterialDesignThemes.Wpf.PackIcon;
                var border = template.FindName("border", btnPauseResume) as System.Windows.Controls.Border;

                if (icon != null)
                {
                    // 切换图标：暂停/播放
                    icon.Kind = showPlayIcon
                        ? MaterialDesignThemes.Wpf.PackIconKind.Play
                        : MaterialDesignThemes.Wpf.PackIconKind.Pause;
                }

                // 切换按钮颜色：播放（黄色）/暂停（蓝色）
                if (border != null)
                {
                    border.Background = new SolidColorBrush(
                        showPlayIcon
                            ? Color.FromRgb(255, 193, 7)   // 黄色
                            : Color.FromRgb(59, 130, 246)  // 蓝色
                    );
                }
            }
        }

        #endregion

        #region 下载器初始化

        /// <summary>
        /// 初始化下载器组件
        /// </summary>
        private void InitializeDownloader()
        {
            downloadManager = new DownloadManager();
            downloadStopwatch = new Stopwatch();

            // 设置窗口图标
            try
            {
                this.Icon = new BitmapImage(new Uri("pack://application:,,,/Resources/Icons/ico.ico"));
            }
            catch
            {
                // 忽略图标加载错误
            }
        }

        #endregion

        #region 下载按钮点击事件

        /// <summary>
        /// 下载按钮点击事件
        /// 处理开始下载、取消下载逻辑
        /// </summary>
        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            Logger.Debug("[LOG] BtnDownload_Click 开始");

            // 如果正在下载，则取消下载
            if (isDownloading)
            {
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

            // 解析下载 URL
            var urls = configManager.ResolveDownloadUrls(selectedName, out string errorMessage);
            Logger.Debug($"[LOG] 解析的 URL 数量：{urls.Count}");

            // 获取保存路径
            string savePath = configManager.GetDefaultDownloadPath();
            if (string.IsNullOrWhiteSpace(savePath))
            {
                savePath = AppDomain.CurrentDomain.BaseDirectory;
            }

            // 验证保存路径
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

            // 创建下载目录
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

            // 验证错误消息
            if (!string.IsNullOrEmpty(errorMessage))
            {
                Logger.Debug($"[LOG] 错误信息：{errorMessage}");
                MessageBox.Show(errorMessage, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 过滤有效的 URL
            var candidateUrls = FilterValidUrls(urls);

            if (candidateUrls.Count == 0)
            {
                MessageBox.Show("所选项目\"" + selectedName + "\"的下载链接无效！\n请检查config.ini文件中的配置。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 尝试下载（支持多个备用 URL）
            await TryDownloadUrls(candidateUrls, savePath);
        }

        /// <summary>
        /// 过滤有效的 URL 列表
        /// </summary>
        /// <param name="urls">原始 URL 列表</param>
        /// <returns>有效的 URL 列表</returns>
        private List<string> FilterValidUrls(List<string> urls)
        {
            var candidateUrls = new List<string>();

            foreach (var url in urls)
            {
                // 检查 URL 格式
                if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                {
                    continue;
                }

                // 只接受 HTTP/HTTPS 协议
                Uri uri = new Uri(url);
                if (uri.Scheme != "http" && uri.Scheme != "https")
                {
                    continue;
                }

                candidateUrls.Add(url);
            }

            return candidateUrls;
        }

        /// <summary>
        /// 尝试下载多个 URL（支持备用地址）
        /// </summary>
        /// <param name="candidateUrls">候选 URL 列表</param>
        /// <param name="savePath">保存路径</param>
        private async Task TryDownloadUrls(List<string> candidateUrls, string savePath)
        {
            Exception lastException = null;

            for (int index = 0; index < candidateUrls.Count; index++)
            {
                var url = candidateUrls[index];
                bool hasMore = index < candidateUrls.Count - 1;

                // 构建连接消息
                string connectingMessage = candidateUrls.Count > 1
                    ? "正在连接... (" + (index + 1) + "/" + candidateUrls.Count + ")"
                    : "正在连接...";

                try
                {
                    activeDownloadTask = StartDownload(url, savePath, connectingMessage, hasMore);
                    await activeDownloadTask;
                    return; // 下载成功，退出
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    // 处理取消或关闭
                    if (ex is OperationCanceledException || IsShuttingDown)
                    {
                        ResetUI();
                        return;
                    }

                    // 准备尝试下一个 URL
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

            // 所有 URL 都失败
            if (lastException != null)
            {
                if (!IsShuttingDown)
                {
                    MessageBox.Show("下载出错：" + lastException.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                ResetUI();
            }
        }

        #endregion

        #region 下载执行

        /// <summary>
        /// 开始下载任务
        /// </summary>
        /// <param name="url">下载 URL</param>
        /// <param name="savePath">保存路径</param>
        /// <param name="connectingMessage">连接消息</param>
        /// <param name="preserveUiOnFailure">失败时是否保留 UI 状态</param>
        /// <returns>下载任务</returns>
        private async Task StartDownload(string url, string savePath, string connectingMessage, bool preserveUiOnFailure)
        {
            Logger.Debug($"[LOG] StartDownload 开始 url={url}");

            if (downloadManager == null)
            {
                throw new InvalidOperationException("下载器未初始化");
            }

            // 重置状态
            cancelRequested = false;
            isDownloading = true;

            // 配置断点续传
            bool enableResume = configManager.GetEnableResume();
            Logger.Debug($"[LOG] 断点续传配置：EnableResume={enableResume}");
            downloadManager.SetEnableResume(enableResume);

            // 更新 UI 状态
            UpdateUiForDownloadStart(connectingMessage);

            // 清除事件状态
            _lastCompletedArgs = null;
            _lastDownloadError = null;

            // 注册事件处理器（使用命名方法防止内存泄漏）
            downloadManager.ProgressChanged += OnDownloadProgressChanged;
            downloadManager.DownloadCompleted += OnDownloadCompleted;
            downloadManager.DownloadError += OnDownloadError;

            bool shouldResetUi = true;

            try
            {
                // 开始下载
                downloadStopwatch.Start();
                bool success = await downloadManager.DownloadFileAsync(url, savePath);
                downloadStopwatch.Stop();

                // 处理取消
                if (_lastCompletedArgs != null && _lastCompletedArgs.Cancelled)
                {
                    throw new OperationCanceledException();
                }

                // 处理失败
                if (!success)
                {
                    if (_lastDownloadError != null)
                    {
                        throw _lastDownloadError;
                    }
                    throw new InvalidOperationException("下载失败");
                }

                // 验证文件
                string finalPath = _lastCompletedArgs?.FilePath;
                if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
                {
                    throw new IOException("下载完成但未找到输出文件");
                }

                // 更新完成状态
                lblProgress.Text = "下载完成";
                progressBar.Value = 100;

                // 检查是否需要自动解压
                if (chkAutoExtract.IsChecked == true && Path.GetExtension(finalPath).ToLower() == ".zip")
                {
                    await ExtractZipFile(finalPath, savePath);
                }

                // 检查是否取消
                if (cancelRequested)
                {
                    throw new OperationCanceledException();
                }

                // 显示完成消息
                if (!IsShuttingDown)
                {
                    MessageBox.Show("下载完成！\n文件已保存到: " + finalPath, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (OperationCanceledException)
            {
                shouldResetUi = true;
                lblProgress.Text = GetCancelMessage(enableResume);
            }
            catch (Exception)
            {
                shouldResetUi = !preserveUiOnFailure;
                lblProgress.Text = "下载失败，已保留未完成文件";
                throw;
            }
            finally
            {
                // 取消注册事件处理器
                downloadManager.ProgressChanged -= OnDownloadProgressChanged;
                downloadManager.DownloadCompleted -= OnDownloadCompleted;
                downloadManager.DownloadError -= OnDownloadError;

                // 清除状态
                _lastCompletedArgs = null;
                _lastDownloadError = null;

                // 重置 UI 或计时器
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

        /// <summary>
        /// 获取取消消息
        /// </summary>
        /// <param name="enableResume">是否启用断点续传</param>
        /// <returns>取消消息</returns>
        private string GetCancelMessage(bool enableResume)
        {
            if (cancelRequested && !enableResume)
            {
                return "操作已取消";
            }

            return enableResume
                ? "下载已取消（已保留未完成文件，可继续下载）"
                : "下载已取消";
        }

        /// <summary>
        /// 更新 UI 为下载开始状态
        /// </summary>
        /// <param name="connectingMessage">连接消息</param>
        private void UpdateUiForDownloadStart(string connectingMessage)
        {
            btnDownload.Content = "取消下载";
            btnDownload.Background = new SolidColorBrush(Color.FromRgb(231, 76, 60));
            btnPauseResume.Visibility = Visibility.Visible;
            btnPauseResume.IsEnabled = true;
            SetPauseResumeIcon(false);
            btnPauseResume.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0));
            progressBar.Value = 0;
            lblProgress.Text = string.IsNullOrWhiteSpace(connectingMessage) ? "正在连接..." : connectingMessage;
            lblSpeed.Text = "";
            isPaused = false;
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 格式化字节数为可读字符串
        /// </summary>
        /// <param name="bytes">字节数</param>
        /// <returns>格式化字符串（如 "1.5 MB"）</returns>
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

        /// <summary>
        /// 重置 UI 到初始状态
        /// </summary>
        private void ResetUI()
        {
            isDownloading = false;
            isPaused = false;
            cancelRequested = false;

            btnDownload.Content = "开始下载";
            btnDownload.Background = new SolidColorBrush(Color.FromRgb(74, 144, 226));
            btnPauseResume.IsEnabled = false;
            btnPauseResume.Visibility = Visibility.Collapsed;
            SetPauseResumeIcon(false);
            btnPauseResume.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0));

            downloadStopwatch.Reset();
        }

        #endregion

        #region ZIP 解压

        /// <summary>
        /// 解压 ZIP 文件
        /// </summary>
        /// <param name="zipFilePath">ZIP 文件路径</param>
        /// <param name="extractPath">解压目标路径</param>
        private async Task ExtractZipFile(string zipFilePath, string extractPath)
        {
            try
            {
                lblProgress.Text = "正在解压ZIP文件...";
                progressBar.Value = 0;

                // 创建进度报告器
                var progress = new Progress<ExtractionProgress>(p =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        progressBar.Value = p.Percentage;
                        lblProgress.Text = $"解压中... ({p.CurrentEntry}/{p.TotalEntries})";
                    });
                });

                // 使用安全解压器
                await SafeZipExtractor.ExtractAsync(zipFilePath, extractPath, progress);

                // 解压完成
                if (!IsShuttingDown && !cancelRequested)
                {
                    lblProgress.Text = "解压完成";
                    progressBar.Value = 100;
                    MessageBox.Show($"ZIP文件解压完成！\n文件已解压到: {extractPath}",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (OperationCanceledException)
            {
                lblProgress.Text = "解压已取消";
            }
            catch (SecurityException ex)
            {
                Logger.Error("解压安全检查失败", ex);
                MessageBox.Show($"解压失败：检测到安全风险 ({ex.Message})",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                lblProgress.Text = "解压失败：安全风险";
            }
            catch (Exception ex)
            {
                Logger.Error("解压失败", ex);
                if (!IsShuttingDown)
                {
                    MessageBox.Show($"解压失败：{ex.Message}",
                        "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                lblProgress.Text = "解压失败";
            }
        }

        #endregion

        #region 下载事件处理器

        /// <summary>
        /// 下载进度变化事件处理器
        /// </summary>
        private void OnDownloadProgressChanged(object sender, DownloadProgressEventArgs e)
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

        /// <summary>
        /// 下载完成事件处理器
        /// </summary>
        private void OnDownloadCompleted(object sender, DownloadCompletedEventArgs e)
        {
            _lastCompletedArgs = e;
        }

        /// <summary>
        /// 下载错误事件处理器
        /// </summary>
        private void OnDownloadError(object sender, DownloadErrorEventArgs e)
        {
            _lastDownloadError = e.Exception;
        }

        #endregion

        #region 窗口关闭处理

        /// <summary>
        /// 窗口关闭事件
        /// 处理下载中的关闭请求
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            // 如果已经准备好关闭，直接关闭
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

            // 如果正在下载，提示用户确认
            if (isDownloading)
            {
                // 防止重复关闭请求
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

                // 开始关闭流程
                e.Cancel = true;
                if (!isClosingInProgress)
                {
                    isClosingInProgress = true;
                    BeginCloseAfterCancelAsync();
                }

                return;
            }

            // 清理下载管理器
            if (downloadManager != null)
            {
                downloadManager.Dispose();
                downloadManager = null;
            }

            base.OnClosing(e);
        }

        /// <summary>
        /// 异步关闭流程
        /// 取消下载后关闭窗口
        /// </summary>
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

        #endregion

        #region 背景图片加载

        /// <summary>
        /// 加载背景图片
        /// 优先从本地加载，支持多背景轮换，失败回退到嵌入资源
        /// </summary>
        private void LoadBackgroundImageFromResource()
        {
            try
            {
                // 尝试加载自定义背景图片
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
                LoadImageFromEmbeddedResource();
            }
            catch (Exception ex)
            {
                Logger.Error("加载背景图片失败", ex);
            }
        }

        /// <summary>
        /// 从文件加载图片
        /// </summary>
        /// <param name="filePath">图片文件路径</param>
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

        /// <summary>
        /// 从嵌入资源加载图片
        /// </summary>
        private void LoadImageFromEmbeddedResource()
        {
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

        #endregion
    }
}