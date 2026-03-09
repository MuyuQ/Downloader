using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace WYDownloader.Core
{
    public static class Logger
    {
        private static readonly object logger;
        private static readonly object fileLock = new object();
        private static readonly MethodInfo debugMethod;
        private static readonly MethodInfo infoMethod;
        private static readonly MethodInfo warnMethod;
        private static readonly MethodInfo errorMethod;

        static Logger()
        {
            try
            {
                // Optional runtime NLog binding. Falls back to Debug output when NLog isn't available.
                var logManagerType = Type.GetType("NLog.LogManager, NLog", throwOnError: false);
                var loggerType = Type.GetType("NLog.Logger, NLog", throwOnError: false);
                if (logManagerType != null && loggerType != null)
                {
                    var getCurrentClassLogger = logManagerType.GetMethod("GetCurrentClassLogger", BindingFlags.Public | BindingFlags.Static);
                    logger = getCurrentClassLogger?.Invoke(null, null);

                    if (logger != null)
                    {
                        debugMethod = loggerType.GetMethod("Debug", new[] { typeof(string) });
                        infoMethod = loggerType.GetMethod("Info", new[] { typeof(string) });
                        warnMethod = loggerType.GetMethod("Warn", new[] { typeof(string) });
                        errorMethod = loggerType.GetMethod("Error", new[] { typeof(string) });
                    }
                }
            }
            catch
            {
                // Ignore reflection setup failures and use Debug fallback.
            }
        }

        public static void Debug(string message)
        {
            if (logger != null && debugMethod != null)
            {
                debugMethod.Invoke(logger, new object[] { message });
                return;
            }

            System.Diagnostics.Debug.WriteLine(message);
            WriteFallbackLog("DEBUG", message);
        }

        public static void Info(string message)
        {
            if (logger != null && infoMethod != null)
            {
                infoMethod.Invoke(logger, new object[] { message });
                return;
            }

            System.Diagnostics.Debug.WriteLine(message);
            WriteFallbackLog("INFO", message);
        }

        public static void Warn(string message)
        {
            if (logger != null && warnMethod != null)
            {
                warnMethod.Invoke(logger, new object[] { message });
                return;
            }

            System.Diagnostics.Debug.WriteLine(message);
            WriteFallbackLog("WARN", message);
        }

        public static void Error(string message, System.Exception ex = null)
        {
            var fullMessage = ex == null ? message : message + " | " + ex;

            if (logger != null && errorMethod != null)
            {
                errorMethod.Invoke(logger, new object[] { fullMessage });
                return;
            }

            System.Diagnostics.Debug.WriteLine(fullMessage);
            WriteFallbackLog("ERROR", fullMessage);
        }

        private static void WriteFallbackLog(string level, string message)
        {
            try
            {
                var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(logDir);

                var logPath = Path.Combine(logDir, "wydownloader-fallback-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + level + " | " + message + Environment.NewLine;

                lock (fileLock)
                {
                    File.AppendAllText(logPath, line);
                }
            }
            catch
            {
                // Keep logging path failure silent to avoid recursive failures.
            }
        }
    }
}
