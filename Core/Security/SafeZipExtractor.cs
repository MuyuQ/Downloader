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
    /// 提供防御性解压功能，防止恶意 ZIP 文件攻击
    /// </summary>
    /// <remarks>
    /// <para>
    /// 安全特性：
    /// <list type="bullet">
    /// <item>路径遍历攻击防护 - 防止解压文件逃逸到目标目录外</item>
    /// <item>压缩炸弹检测 - 检测异常高压缩比的文件</item>
    /// <item>危险文件过滤 - 过滤可执行文件等危险类型</item>
    /// <item>文件大小限制 - 限制单个文件和总体积</item>
    /// <item>文件数量限制 - 防止 ZIP 包含大量小文件导致资源耗尽</item>
    /// <item>Windows 保留名检测 - 防止创建保留设备名文件</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static class SafeZipExtractor
    {
        #region 常量

        /// <summary>
        /// 危险文件扩展名黑名单
        /// 这些扩展名的文件可能包含可执行代码或脚本
        /// </summary>
        private static readonly HashSet<string> DangerousExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            // Windows 可执行文件
            ".exe", ".dll", ".com", ".scr", ".pif", ".bat", ".cmd",
            // 脚本文件
            ".sh", ".ps1", ".vbs", ".js", ".wsf", ".hta",
            // 其他可执行文件
            ".jar", ".msi", ".app", ".dmg"
        };

        /// <summary>
        /// 最大允许的单个文件大小 (100MB)
        /// </summary>
        private const long MAX_FILE_SIZE = 100 * 1024 * 1024;

        /// <summary>
        /// 最大压缩比
        /// 用于检测压缩炸弹（解压后体积异常大）
        /// </summary>
        private const double MAX_COMPRESSION_RATIO = 100.0;

        /// <summary>
        /// 最大文件数量
        /// 防止 ZIP 包含过多文件导致资源耗尽
        /// </summary>
        private const int MAX_FILE_COUNT = 10000;

        #endregion

        #region 公共方法

        /// <summary>
        /// 安全地解压 ZIP 文件
        /// </summary>
        /// <param name="zipFilePath">ZIP 文件路径</param>
        /// <param name="extractPath">解压目标路径</param>
        /// <param name="progress">进度报告器（可选）</param>
        /// <param name="cancellationToken">取消令牌（可选）</param>
        /// <exception cref="FileNotFoundException">ZIP 文件不存在</exception>
        /// <exception cref="SecurityException">安全检查失败</exception>
        /// <exception cref="OperationCanceledException">操作被取消</exception>
        public static async Task ExtractAsync(
            string zipFilePath,
            string extractPath,
            IProgress<ExtractionProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            // 验证 ZIP 文件存在
            if (!File.Exists(zipFilePath))
            {
                throw new FileNotFoundException("ZIP 文件不存在", zipFilePath);
            }

            // 规范化并创建目标目录
            extractPath = Path.GetFullPath(extractPath);
            Directory.CreateDirectory(extractPath);

            // 打开 ZIP 归档
            using (var archive = ZipFile.OpenRead(zipFilePath))
            {
                // 执行安全验证
                ValidateArchive(archive);

                // 获取安全的条目列表
                var safeEntries = GetSafeEntries(archive, extractPath);

                int processedCount = 0;

                // 逐个解压文件
                foreach (var (entry, safePath) in safeEntries)
                {
                    // 检查取消请求
                    cancellationToken.ThrowIfCancellationRequested();

                    // 创建目标目录
                    var directory = Path.GetDirectoryName(safePath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // 异步解压单个文件
                    await ExtractEntryAsync(entry, safePath, cancellationToken);

                    // 报告进度
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

        #endregion

        #region 私有方法 - 安全验证

        /// <summary>
        /// 验证 ZIP 归档的安全性
        /// </summary>
        /// <param name="archive">ZIP 归档对象</param>
        /// <exception cref="SecurityException">安全检查失败</exception>
        private static void ValidateArchive(ZipArchive archive)
        {
            // 检查文件数量
            if (archive.Entries.Count > MAX_FILE_COUNT)
            {
                throw new SecurityException(
                    $"ZIP 文件包含过多条目: {archive.Entries.Count} (最大允许: {MAX_FILE_COUNT})");
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
            // 压缩炸弹：压缩率异常高的文件，解压后会占用大量空间
            if (totalCompressed > 0)
            {
                double ratio = (double)totalUncompressed / totalCompressed;
                if (ratio > MAX_COMPRESSION_RATIO)
                {
                    throw new SecurityException(
                        $"检测到 ZIP 炸弹 (压缩比: {ratio:F1})");
                }
            }

            // 检查总大小（限制 1GB）
            if (totalUncompressed > MAX_FILE_SIZE * 10)
            {
                throw new SecurityException("ZIP 内容总体过大");
            }
        }

        /// <summary>
        /// 获取安全的条目列表
        /// 过滤掉不安全的文件条目
        /// </summary>
        /// <param name="archive">ZIP 归档对象</param>
        /// <param name="extractRoot">解压根目录</param>
        /// <returns>安全的条目列表（条目和目标路径的元组）</returns>
        private static List<(ZipArchiveEntry entry, string safePath)> GetSafeEntries(
            ZipArchive archive,
            string extractRoot)
        {
            var result = new List<(ZipArchiveEntry, string)>();

            foreach (var entry in archive.Entries)
            {
                // 跳过目录条目（没有文件名的条目）
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
        /// <param name="entry">ZIP 条目</param>
        /// <param name="extractRoot">解压根目录</param>
        /// <param name="safePath">输出：安全的目标路径</param>
        /// <returns>是否安全</returns>
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

                #region 路径遍历检查

                // 路径遍历攻击防护
                // 确保 destinationPath 在 extractRoot 目录内
                // 防止恶意条目如 "../../../Windows/System32/exploit.dll"
                if (!destinationPath.StartsWith(fullExtractRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"阻止路径遍历攻击: {entry.FullName} -> {destinationPath}");
                    return false;
                }

                #endregion

                #region 文件扩展名检查

                // 检查文件扩展名
                string extension = Path.GetExtension(entry.Name);
                if (DangerousExtensions.Contains(extension))
                {
                    Logger.Warn($"阻止危险文件类型: {entry.Name}");
                    return false;
                }

                #endregion

                #region 文件大小检查

                // 检查单个文件大小
                if (entry.Length > MAX_FILE_SIZE)
                {
                    Logger.Warn($"文件过大: {entry.Name} ({entry.Length} bytes)");
                    return false;
                }

                #endregion

                #region 文件名检查

                // 检查文件名长度
                if (entry.Name.Length > 255)
                {
                    Logger.Warn($"文件名过长: {entry.Name}");
                    return false;
                }

                // 检查 Windows 保留文件名
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(entry.Name);
                if (IsReservedName(fileNameWithoutExt))
                {
                    Logger.Warn($"阻止保留文件名: {entry.Name}");
                    return false;
                }

                #endregion

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
        /// <param name="name">文件名（不含扩展名）</param>
        /// <returns>是否为保留名</returns>
        /// <remarks>
        /// Windows 保留以下文件名，不能用作文件名：
        /// CON, PRN, AUX, NUL, COM1-COM9, LPT1-LPT9
        /// </remarks>
        private static bool IsReservedName(string name)
        {
            string[] reservedNames =
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };

            return reservedNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region 私有方法 - 解压执行

        /// <summary>
        /// 解压单个条目
        /// 使用临时文件确保原子性
        /// </summary>
        /// <param name="entry">ZIP 条目</param>
        /// <param name="destinationPath">目标路径</param>
        /// <param name="cancellationToken">取消令牌</param>
        private static async Task ExtractEntryAsync(
            ZipArchiveEntry entry,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            // 使用临时文件确保原子性
            // 这样可以防止解压过程中出错导致目标文件损坏
            string tempPath = destinationPath + ".tmp" + Guid.NewGuid().ToString("N");

            try
            {
                // 异步复制文件内容
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

                // 原子性移动：删除已存在的文件，移动临时文件
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

        #endregion
    }

    #region 进度报告类

    /// <summary>
    /// 解压进度信息
    /// </summary>
    public class ExtractionProgress
    {
        /// <summary>
        /// 当前已处理的条目索引
        /// </summary>
        public int CurrentEntry { get; set; }

        /// <summary>
        /// 总条目数
        /// </summary>
        public int TotalEntries { get; set; }

        /// <summary>
        /// 当前处理的文件名
        /// </summary>
        public string CurrentFileName { get; set; }

        /// <summary>
        /// 完成百分比 (0-100)
        /// </summary>
        public double Percentage => TotalEntries > 0 ? (double)CurrentEntry / TotalEntries * 100 : 0;
    }

    #endregion
}