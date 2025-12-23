using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Reflection;

namespace WYDownloader
{
    public class ConfigManager
    {
        private string configContent;
        private Dictionary<string, Dictionary<string, string>> configData;

        public ConfigManager()
        {
            // 直接从嵌入资源中读取配置，不创建外部文件
            LoadEmbeddedConfig();
        }

        private void LoadEmbeddedConfig()
        {
            configContent = GetEmbeddedConfigContent();
            if (string.IsNullOrEmpty(configContent))
            {
                // 如果无法读取嵌入资源，使用硬编码的默认配置
                configContent = @"[Settings]
; 自动解压ZIP文件 (true/false)
AutoExtractZip=true

; 默认选择的下载项目（对应Downloads节中的键名）
DefaultDownload=Keira工具

[Downloads]
; 下载链接配置格式：名称=URL
; 可以添加多个下载链接，每行一个
示例文件=https://httpbin.org/json
测试ZIP文件=https://github.com/microsoft/vscode/archive/refs/heads/main.zip
Keira工具=https://fly.qingyuji.cn/f/d/B0jWHm/Keira-3.10.3.WINDO exe

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

            // 解析配置内容到内存中
            ParseConfigContent();
        }

        private string GetEmbeddedConfigContent()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = "WYDownloader.config.ini";

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
            catch (Exception)
            {
                // 如果读取失败，返回null，使用fallback配置
            }

            return null;
        }

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

                // 跳过空行和注释
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith(";"))
                    continue;

                // 检查是否是节标题
                if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                {
                    currentSection = trimmedLine.Substring(1, trimmedLine.Length - 2);
                    if (!configData.ContainsKey(currentSection))
                    {
                        configData[currentSection] = new Dictionary<string, string>();
                    }
                    continue;
                }

                // 解析键值对
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
            catch (Exception)
            {
                // 如果读取失败，返回默认值
            }

            return defaultValue;
        }

        public void WriteValue(string section, string key, string value)
        {
            // 由于配置是从嵌入资源读取的，不支持运行时修改
            // 如果需要支持用户自定义配置，可以考虑使用注册表或用户配置文件
        }

        public Dictionary<string, string> GetDownloadLinks()
        {
            var downloads = new Dictionary<string, string>();

            try
            {
                if (configData != null && configData.ContainsKey("Downloads"))
                {
                    foreach (var kvp in configData["Downloads"])
                    {
                        if (!string.IsNullOrEmpty(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
                        {
                            downloads[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 如果读取失败，返回默认值
            }

            // 如果没有配置或读取失败，返回默认值
            if (downloads.Count == 0)
            {
                downloads["示例文件"] = "https://httpbin.org/json";
                downloads["测试ZIP文件"] = "https://github.com/microsoft/vscode/archive/refs/heads/main.zip";
            }

            return downloads;
        }

        public string GetDefaultDownload()
        {
            return ReadValue("Settings", "DefaultDownload", "示例文件");
        }

        public string GetDownloadUrl(string downloadName)
        {
            var downloads = GetDownloadLinks();
            return downloads.ContainsKey(downloadName) ? downloads[downloadName] : "";
        }

        public bool GetAutoExtractZip()
        {
            string value = ReadValue("Settings", "AutoExtractZip", "true");
            return value.ToLower() == "true";
        }

        public string GetAnnouncementTitle()
        {
            return ReadValue("Announcement", "Title", "WY直链下载器");
        }

        public string[] GetAnnouncementContent()
        {
            string content = ReadValue("Announcement", "Content", "欢迎使用WY直链下载器！");
            return content.Split('|');
        }

        public int GetWindowWidth()
        {
            string value = ReadValue("UI", "WindowWidth", "900");
            int width;
            return int.TryParse(value, out width) ? width : 900;
        }

        public int GetWindowHeight()
        {
            string value = ReadValue("UI", "WindowHeight", "600");
            int height;
            return int.TryParse(value, out height) ? height : 600;
        }
    }
}
