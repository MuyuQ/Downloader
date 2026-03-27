using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using WYDownloader.Core;

namespace WYDownloader
{
    /// <summary>
    /// 应用程序入口类
    /// 负责应用程序的启动、关闭和全局异常处理
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 应用程序启动事件
        /// 在应用程序启动时执行初始化操作
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 记录应用程序启动日志
            Logger.Info("========================================");
            Logger.Info("WYDownloader 应用程序启动");
            Logger.Info($"启动时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Logger.Info($"命令行参数数量: {e.Args.Length}");
            Logger.Info("========================================");

            // 注册全局异常处理器
            SetupExceptionHandlers();
        }

        /// <summary>
        /// 应用程序退出事件
        /// 在应用程序关闭时执行清理操作
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            // 记录应用程序退出日志
            Logger.Info("========================================");
            Logger.Info("WYDownloader 应用程序退出");
            Logger.Info($"退出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Logger.Info($"退出代码: {e.ApplicationExitCode}");
            Logger.Info("========================================");

            base.OnExit(e);
        }

        /// <summary>
        /// 设置全局异常处理器
        /// 捕获所有未处理的异常并记录日志
        /// </summary>
        private void SetupExceptionHandlers()
        {
            // 处理 UI 线程未捕获的异常
            // 当 WPF 应用程序的 UI 线程发生未处理异常时触发
            this.DispatcherUnhandledException += (sender, e) =>
            {
                Logger.Error("UI 线程未捕获异常", e.Exception);
                ShowErrorMessage("应用程序发生错误", e.Exception.Message);
                e.Handled = true; // 标记为已处理，防止应用程序崩溃
            };

            // 处理后台线程未捕获的异常
            // 当 Task 中的异常未被观察时触发
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Logger.Error("后台任务未观察异常", e.Exception);
                e.SetObserved(); // 标记为已观察，防止应用程序崩溃
            };

            // 处理非 UI 线程未捕获的异常
            // 当非 UI 线程发生未处理异常时触发
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                var exception = e.ExceptionObject as Exception;
                Logger.Error("非 UI 线程未捕获异常", exception);

                // 如果异常对象存在，显示错误消息
                if (exception != null)
                {
                    ShowErrorMessage("应用程序发生严重错误", exception.Message);
                }
            };
        }

        /// <summary>
        /// 显示错误消息对话框
        /// 在主窗口可用时显示模态对话框，否则使用消息框
        /// </summary>
        /// <param name="title">错误标题</param>
        /// <param name="message">错误消息</param>
        private void ShowErrorMessage(string title, string message)
        {
            // 确保在 UI 线程上执行
            Dispatcher.Invoke(() =>
            {
                // 检查主窗口是否可用
                if (MainWindow != null && MainWindow.IsLoaded)
                {
                    MessageBox.Show(MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }
    }
}