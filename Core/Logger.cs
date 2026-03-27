using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace WYDownloader.Core
{
    /// <summary>
    /// 日志记录器
    /// 提供应用程序的日志记录功能，支持 NLog 和回退日志两种模式
    /// 当 NLog 可用时使用 NLog，否则使用文件回退日志
    /// </summary>
    /// <remarks>
    /// 日志级别说明：
    /// - Debug: 调试信息，仅在调试时使用
    /// - Info: 常规信息，记录应用程序运行状态
    /// - Warn: 警告信息，记录潜在问题
    /// - Error: 错误信息，记录异常和错误
    /// </remarks>
    public static class Logger
    {
        #region 私有字段

        /// <summary>
        /// NLog Logger 实例（通过反射获取）
        /// </summary>
        private static readonly object logger;

        /// <summary>
        /// 文件写入锁，确保多线程安全
        /// </summary>
        private static readonly object fileLock = new object();

        /// <summary>
        /// NLog Debug 方法信息
        /// </summary>
        private static readonly MethodInfo debugMethod;

        /// <summary>
        /// NLog Info 方法信息
        /// </summary>
        private static readonly MethodInfo infoMethod;

        /// <summary>
        /// NLog Warn 方法信息
        /// </summary>
        private static readonly MethodInfo warnMethod;

        /// <summary>
        /// NLog Error 方法信息
        /// </summary>
        private static readonly MethodInfo errorMethod;

        #endregion

        #region 静态构造函数

        /// <summary>
        /// 静态构造函数
        /// 初始化 NLog 反射绑定，失败时回退到 Debug 输出
        /// </summary>
        static Logger()
        {
            try
            {
                // 尝试通过反射绑定 NLog
                // 使用反射可以避免对 NLog 的硬依赖，使程序在没有 NLog 时也能运行
                var logManagerType = Type.GetType("NLog.LogManager, NLog", throwOnError: false);
                var loggerType = Type.GetType("NLog.Logger, NLog", throwOnError: false);

                if (logManagerType != null && loggerType != null)
                {
                    // 获取 GetCurrentClassLogger 方法
                    var getCurrentClassLogger = logManagerType.GetMethod(
                        "GetCurrentClassLogger",
                        BindingFlags.Public | BindingFlags.Static);

                    // 调用 GetCurrentClassLogger 获取 logger 实例
                    logger = getCurrentClassLogger?.Invoke(null, null);

                    if (logger != null)
                    {
                        // 获取各个日志级别的方法
                        debugMethod = loggerType.GetMethod("Debug", new[] { typeof(string) });
                        infoMethod = loggerType.GetMethod("Info", new[] { typeof(string) });
                        warnMethod = loggerType.GetMethod("Warn", new[] { typeof(string) });
                        errorMethod = loggerType.GetMethod("Error", new[] { typeof(string) });
                    }
                }
            }
            catch
            {
                // 忽略反射设置失败，使用 Debug 回退
                // 这样设计是为了保证程序在没有 NLog 的情况下也能正常工作
            }
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 记录调试级别日志
        /// 用于开发调试，记录详细的程序执行流程
        /// </summary>
        /// <param name="message">日志消息</param>
        public static void Debug(string message)
        {
            // 优先使用 NLog
            if (logger != null && debugMethod != null)
            {
                debugMethod.Invoke(logger, new object[] { message });
                return;
            }

            // 回退到 Debug 输出
            System.Diagnostics.Debug.WriteLine(message);
            WriteFallbackLog("DEBUG", message);
        }

        /// <summary>
        /// 记录信息级别日志
        /// 用于记录应用程序的正常运行状态
        /// </summary>
        /// <param name="message">日志消息</param>
        public static void Info(string message)
        {
            // 优先使用 NLog
            if (logger != null && infoMethod != null)
            {
                infoMethod.Invoke(logger, new object[] { message });
                return;
            }

            // 回退到 Debug 输出
            System.Diagnostics.Debug.WriteLine(message);
            WriteFallbackLog("INFO", message);
        }

        /// <summary>
        /// 记录警告级别日志
        /// 用于记录潜在问题，不影响程序正常运行
        /// </summary>
        /// <param name="message">日志消息</param>
        public static void Warn(string message)
        {
            // 优先使用 NLog
            if (logger != null && warnMethod != null)
            {
                warnMethod.Invoke(logger, new object[] { message });
                return;
            }

            // 回退到 Debug 输出
            System.Diagnostics.Debug.WriteLine(message);
            WriteFallbackLog("WARN", message);
        }

        /// <summary>
        /// 记录错误级别日志
        /// 用于记录异常和错误，需要关注和处理
        /// </summary>
        /// <param name="message">日志消息</param>
        /// <param name="ex">异常对象（可选）</param>
        public static void Error(string message, Exception ex = null)
        {
            // 组合完整的错误消息
            var fullMessage = ex == null ? message : message + " | " + ex;

            // 优先使用 NLog
            if (logger != null && errorMethod != null)
            {
                errorMethod.Invoke(logger, new object[] { fullMessage });
                return;
            }

            // 回退到 Debug 输出
            System.Diagnostics.Debug.WriteLine(fullMessage);
            WriteFallbackLog("ERROR", fullMessage);
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 写入回退日志文件
        /// 当 NLog 不可用时，将日志写入本地文件
        /// </summary>
        /// <param name="level">日志级别</param>
        /// <param name="message">日志消息</param>
        /// <remarks>
        /// 日志文件命名格式：wydownloader-fallback-yyyyMMdd.log
        /// 日志内容格式：yyyy-MM-dd HH:mm:ss.fff | LEVEL | message
        /// </remarks>
        private static void WriteFallbackLog(string level, string message)
        {
            try
            {
                // 构建日志目录路径
                var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(logDir);

                // 构建日志文件路径（按日期命名）
                var logPath = Path.Combine(logDir,
                    "wydownloader-fallback-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

                // 格式化日志行
                var line = string.Format("{0} | {1} | {2}{3}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    level,
                    message,
                    Environment.NewLine);

                // 使用锁确保线程安全的文件写入
                lock (fileLock)
                {
                    File.AppendAllText(logPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // 保持日志路径失败静默，避免递归失败
                // 如果日志写入失败，不应影响程序正常运行
            }
        }

        #endregion
    }
}