using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection;

namespace WYDownloader.Core
{
    /// <summary>
    /// 配置管理器
    /// 负责读取和管理应用程序配置，支持从本地文件和嵌入资源读取
    /// </summary>
    /// <remarks>
    /// 配置文件格式为 INI 风格：
    /// [Section]
    /// Key=Value
    ///
    /// 配置加载优先级：
    /// 1. 本地配置文件（Resources/Config/config.ini）
    /// 2. 嵌入资源（Resources.Config.config.ini）
    /// 3. 硬编码默认配置
    /// </remarks>
    public class ConfigManager
    {
        #region 私有字段

        /// <summary>
        /// 配置文件原始内容
        /// </summary>
        private string configContent;

        /// <summary>
        /// 解析后的配置数据
        /// 结构：Section -> (Key -> Value)
        /// </summary>
        private Dictionary<string, Dictionary<string, string>> configData;

        /// <summary>
        /// 本地配置文件路径
        /// </summary>
        private readonly string localConfigPath;

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化配置管理器
        /// 设置配置文件路径并加载配置
        /// </summary>
        public ConfigManager()
        {
            // 本地配置文件路径：程序目录/Resources/Config/config.ini
            localConfigPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Resources", "Config", "config.ini");

            LoadConfig();
        }

        #endregion

        #region 配置加载

        /// <summary>
        /// 加载配置内容
        /// 按优先级依次尝试：本地文件 -> 嵌入资源 -> 默认配置
        /// </summary>
        private void LoadConfig()
        {
            // 尝试从本地文件加载
            configContent = GetLocalConfigContent() ?? GetEmbeddedConfigContent();

            // 如果都失败，使用硬编码默认配置
            if (string.IsNullOrEmpty(configContent))
            {
                configContent = GetDefaultConfigContent();
            }

            // 解析配置内容到内存
            ParseConfigContent();
        }

        /// <summary>
        /// 从本地文件读取配置内容
        /// </summary>
        /// <returns>配置文件内容，失败返回 null</returns>
        private string GetLocalConfigContent()
        {
            try
            {
                if (File.Exists(localConfigPath))
                {
                    return File.ReadAllText(localConfigPath, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读取本地配置失败，将尝试嵌入配置: " + ex.Message);
            }

            return null;
        }

        /// <summary>
        /// 从嵌入资源读取配置内容
        /// </summary>
        /// <returns>配置文件内容，失败返回 null</returns>
        private string GetEmbeddedConfigContent()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = "WYDownloader.Resources.Config.config.ini";

                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            return reader.ReadToEnd();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读取嵌入配置失败，将尝试默认配置: " + ex.Message);
            }

            return null;
        }

        /// <summary>
        /// 获取硬编码的默认配置内容
        /// </summary>
        /// <returns>默认配置字符串</returns>
        private string GetDefaultConfigContent()
        {
            // 硬编码默认配置，确保程序在配置文件缺失时仍可运行
            return @"[Settings]
; 自动解压ZIP文件 (true/false)
AutoExtractZip=true

; 默认选择的下载项目（对应Downloads节中的键名）
DefaultDownload=Keira工具

; 断点续传开关 (true/false)
EnableResume=true

; 默认下载路径（为空则使用程序所在目录）
DefaultDownloadPath=

; 背景图片列表（多个用 | 分隔）
BackgroundImages=

[Servers]
; 下载资源服务器地址（用于相对路径，可选）
BaseUrl=
; 备用镜像地址（可选，多个用 | 分隔）
Mirrors=

[Downloads]
; 下载链接配置格式：名称=URL
; 可以添加多个下载链接，每行一个
示例文件=https://httpbin.org/json
测试ZIP文件=https://github.com/microsoft/vscode/archive/refs/heads/main.zip
Keira工具=https://gitee.com/wyark/keira3/releases/download/Keira3%E6%B1%89%E5%8C%96%E7%89%88/Keira-3.10.3.WINDOWS.exe%20(1).zip

[Announcement]
; 公告标题
Title=WY直链下载器

; 公告内容 (支持多行，用 | 分隔)
Content=欢迎使用WY直链下载器！|全新优化的公告显示界面|• 支持HTTP/HTTPS协议的直链下载|• 实时显示下载进度、速度和文件大小|• 支持ZIP文件自动解压功能|• 支持暂停/继续下载操作|• 文件将保存到程序所在目录|• 可通过config.ini配置下载链接和公告内容|• 现代化半透明界面设计|• 支持多个下载链接快速切换

[UI]
; 界面配置
WindowWidth=900
WindowHeight=600";
        }

        /// <summary>
        /// 解析配置内容到内存字典
        /// 支持 INI 格式：[Section] 和 Key=Value
        /// </summary>
        private void ParseConfigContent()
        {
            configData = new Dictionary<string, Dictionary<string, string>>();

            if (string.IsNullOrEmpty(configContent))
                return;

            var lines = configContent.Split('\n');
            string currentSection = "";

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // 跳过空行和注释行（以 ; 开头）
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith(";"))
                    continue;

                // 检查是否是节标题 [Section]
                if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                {
                    // 提取节名
                    currentSection = trimmedLine.Substring(1, trimmedLine.Length - 2);
                    if (!configData.ContainsKey(currentSection))
                    {
                        configData[currentSection] = new Dictionary<string, string>();
                    }
                    continue;
                }

                // 解析键值对 Key=Value
                if (!string.IsNullOrEmpty(currentSection) && trimmedLine.Contains("="))
                {
                    var parts = trimmedLine.Split(new char[] { '=' }, 2);
                    if (parts.Length == 2)
                    {
                        var key = parts[0].Trim();
                        var value = parts[1].Trim();
                        configData[currentSection][key] = value;
                    }
                }
            }
        }

        #endregion

        #region 基础读写方法

        /// <summary>
        /// 读取指定节和键的字符串值
        /// </summary>
        /// <param name="section">节名称</param>
        /// <param name="key">键名称</param>
        /// <param name="defaultValue">默认值</param>
        /// <returns>配置值或默认值</returns>
        public string ReadValue(string section, string key, string defaultValue = "")
        {
            try
            {
                if (configData != null &&
                    configData.ContainsKey(section) &&
                    configData[section].ContainsKey(key))
                {
                    return configData[section][key];
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读取配置键值失败，返回默认值: " + ex.Message);
            }

            return defaultValue;
        }

        /// <summary>
        /// 写入指定节和键的值
        /// 注意：当前实现为空，配置仅支持读取
        /// </summary>
        /// <param name="section">节名称</param>
        /// <param name="key">键名称</param>
        /// <param name="value">值</param>
        /// <remarks>
        /// 如果需要支持运行时修改配置，可以考虑：
        /// 1. 写入用户配置文件（AppData 目录）
        /// 2. 使用注册表存储
        /// 3. 使用 Properties.Settings
        /// </remarks>
        public void WriteValue(string section, string key, string value)
        {
            // 当前实现为空
            // 配置从嵌入资源读取，不支持运行时修改
            // 如需支持用户自定义配置，可考虑使用独立的用户配置文件
        }

        #endregion

        #region 下载配置

        /// <summary>
        /// 获取所有下载链接配置
        /// </summary>
        /// <returns>下载链接字典：名称 -> URL</returns>
        public Dictionary<string, string> GetDownloadLinks()
        {
            var downloads = new Dictionary<string, string>();

            try
            {
                if (configData != null && configData.ContainsKey("Downloads"))
                {
                    foreach (var kvp in configData["Downloads"])
                    {
                        // 过滤空键值
                        if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                        {
                            downloads[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读取下载链接失败，返回默认值: " + ex.Message);
            }

            // 如果没有配置或读取失败，返回默认值
            if (downloads.Count == 0)
            {
                downloads["示例文件"] = "https://httpbin.org/json";
                downloads["测试ZIP文件"] = "https://github.com/microsoft/vscode/archive/refs/heads/main.zip";
            }

            return downloads;
        }

        /// <summary>
        /// 获取默认选择的下载项目名称
        /// </summary>
        /// <returns>默认下载项目名称</returns>
        public string GetDefaultDownload()
        {
            return ReadValue("Settings", "DefaultDownload", "示例文件");
        }

        /// <summary>
        /// 根据名称获取下载 URL
        /// </summary>
        /// <param name="downloadName">下载项目名称</param>
        /// <returns>下载 URL，不存在返回空字符串</returns>
        public string GetDownloadUrl(string downloadName)
        {
            var downloads = GetDownloadLinks();
            return downloads.ContainsKey(downloadName) ? downloads[downloadName] : "";
        }

        /// <summary>
        /// 解析下载 URL 列表
        /// 支持绝对 URL 和相对路径（结合服务器地址）
        /// </summary>
        /// <param name="downloadName">下载项目名称</param>
        /// <param name="errorMessage">错误消息输出</param>
        /// <returns>解析后的 URL 列表</returns>
        /// <remarks>
        /// URL 解析规则：
        /// 1. 如果是绝对 URL（http:// 或 https://），直接返回
        /// 2. 如果是相对路径，结合 BaseUrl 和 Mirrors 生成完整 URL
        /// 3. 返回的 URL 列表已去重
        /// </remarks>
        public List<string> ResolveDownloadUrls(string downloadName, out string errorMessage)
        {
            errorMessage = "";
            var rawUrl = GetDownloadUrl(downloadName);

            // 检查 URL 是否为空
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                errorMessage = "所选项目\"" + downloadName + "\"的下载链接为空！\n请检查config.ini文件中的配置。";
                return new List<string>();
            }

            // 如果是绝对 URL，直接返回
            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var absoluteUri))
            {
                return new List<string> { absoluteUri.ToString() };
            }

            // 如果包含协议但解析失败，说明是无效 URL
            if (rawUrl.Contains("://"))
            {
                errorMessage = "所选项目\"" + downloadName + "\"的下载链接无效！\n请检查config.ini文件中的配置。";
                return new List<string>();
            }

            // 处理相对路径：结合服务器地址
            var serverUrls = new List<string>();

            // 添加主服务器地址
            var baseUrl = GetServerBaseUrl();
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                serverUrls.Add(baseUrl);
            }

            // 添加镜像服务器地址
            serverUrls.AddRange(GetServerMirrors());

            // 构建完整 URL 列表
            var resolvedUrls = new List<string>();
            foreach (var serverUrl in serverUrls)
            {
                if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var baseUri))
                {
                    continue;
                }

                // 只接受 HTTP/HTTPS 协议
                if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
                {
                    continue;
                }

                // 确保基础 URL 以斜杠结尾
                var baseUriString = baseUri.AbsoluteUri;
                if (!baseUriString.EndsWith("/"))
                {
                    baseUriString += "/";
                }

                // 组合完整 URL
                var normalizedBaseUri = new Uri(baseUriString);
                if (Uri.TryCreate(normalizedBaseUri, rawUrl, out var resolvedUri))
                {
                    resolvedUrls.Add(resolvedUri.ToString());
                }
            }

            // 去重
            resolvedUrls = resolvedUrls.Distinct().ToList();

            // 如果没有有效的 URL，返回错误
            if (resolvedUrls.Count == 0)
            {
                errorMessage = "所选项目\"" + downloadName + "\"使用了相对路径，但未配置有效的服务器地址。\n请在config.ini文件中设置[Servers]的BaseUrl或Mirrors。";
            }

            return resolvedUrls;
        }

        /// <summary>
        /// 获取默认下载路径
        /// </summary>
        /// <returns>下载路径，未配置时返回程序所在目录</returns>
        public string GetDefaultDownloadPath()
        {
            string value = ReadValue("Settings", "DefaultDownloadPath", "");
            if (string.IsNullOrWhiteSpace(value))
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
            return value;
        }

        #endregion

        #region 服务器配置

        /// <summary>
        /// 获取服务器基础 URL
        /// </summary>
        /// <returns>基础 URL，未配置返回空字符串</returns>
        public string GetServerBaseUrl()
        {
            return ReadValue("Servers", "BaseUrl", "");
        }

        /// <summary>
        /// 获取服务器镜像地址列表
        /// </summary>
        /// <returns>镜像地址列表</returns>
        public List<string> GetServerMirrors()
        {
            var value = ReadValue("Servers", "Mirrors", "");
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            // 使用 | 分隔多个镜像地址
            return value.Split('|')
                .Select(mirror => mirror.Trim())
                .Where(mirror => !string.IsNullOrWhiteSpace(mirror))
                .ToList();
        }

        #endregion

        #region 功能配置

        /// <summary>
        /// 获取是否启用自动解压 ZIP
        /// </summary>
        /// <returns>true 表示启用，false 表示禁用</returns>
        public bool GetAutoExtractZip()
        {
            string value = ReadValue("Settings", "AutoExtractZip", "true");
            return value.ToLower() == "true";
        }

        /// <summary>
        /// 获取是否启用断点续传
        /// </summary>
        /// <returns>true 表示启用，false 表示禁用</returns>
        public bool GetEnableResume()
        {
            string value = ReadValue("Settings", "EnableResume", "true");
            return value.ToLower() == "true";
        }

        #endregion

        #region 公告配置

        /// <summary>
        /// 获取公告标题
        /// </summary>
        /// <returns>公告标题</returns>
        public string GetAnnouncementTitle()
        {
            return ReadValue("Announcement", "Title", "WY直链下载器");
        }

        /// <summary>
        /// 获取公告内容
        /// </summary>
        /// <returns>公告内容数组，使用 | 分隔</returns>
        public string[] GetAnnouncementContent()
        {
            string content = ReadValue("Announcement", "Content", "欢迎使用WY直链下载器！");
            return content.Split('|');
        }

        #endregion

        #region UI 配置

        /// <summary>
        /// 获取窗口宽度
        /// </summary>
        /// <returns>窗口宽度（像素），默认 900</returns>
        public int GetWindowWidth()
        {
            string value = ReadValue("UI", "WindowWidth", "900");
            int width;
            return int.TryParse(value, out width) ? width : 900;
        }

        /// <summary>
        /// 获取窗口高度
        /// </summary>
        /// <returns>窗口高度（像素），默认 600</returns>
        public int GetWindowHeight()
        {
            string value = ReadValue("UI", "WindowHeight", "600");
            int height;
            return int.TryParse(value, out height) ? height : 600;
        }

        /// <summary>
        /// 获取背景图片列表
        /// </summary>
        /// <returns>背景图片文件名列表</returns>
        public List<string> GetBackgroundImages()
        {
            var value = ReadValue("Settings", "BackgroundImages", "");
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            // 使用 | 分隔多个背景图片
            return value.Split('|')
                .Select(img => img.Trim())
                .Where(img => !string.IsNullOrWhiteSpace(img))
                .ToList();
        }

        #endregion
    }
}