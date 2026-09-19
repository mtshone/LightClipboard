using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LightClipboard.Services;
using LightClipboard.Views;

namespace LightClipboard;

/// <summary>
/// 应用入口：单实例、托盘常驻、按需弹出面板。
/// 支持的命令行参数：
///   --startup    由开机自启拉起，启动后不弹出面板
///   --hidden     启动后不弹出面板
///   --demo       写入演示数据（用于界面/功能自检）
///   --selftest   运行无界面自检并输出报告后退出
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\LightClipboard.SingleInstance.v1";
    private const string ShowEventName = @"Local\LightClipboard.ShowPanel.v1";

    private AppHost? _host;
    private MainWindow? _window;
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string[] args = e.Args ?? Array.Empty<string>();
        bool selfTest = HasFlag(args, "--selftest");

        if (selfTest)
        {
            // 自检使用独立的临时数据目录，避免污染用户真实历史
            Environment.SetEnvironmentVariable(
                "LIGHTCLIPBOARD_HOME",
                Path.Combine(Path.GetTempPath(), "LightClipboard-SelfTest"));
        }

        Log.Initialize();
        AppPaths.EnsureCreated();

        if (selfTest)
        {
            int exitCode = SelfTest.Run(AppPaths.SelfTestReportPath);
            Shutdown(exitCode);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            Log.Error("未处理的异常", eventArgs.ExceptionObject as Exception);

        // ---------- 单实例 ----------
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Log.Info("检测到已有实例，通知其显示面板后退出");
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch (Exception ex)
            {
                Log.Warn("通知已有实例失败", ex);
            }

            Shutdown(0);
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _window?.ShowPanel()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        // ---------- 服务与界面 ----------
        try
        {
            _host = new AppHost();
        }
        catch (Exception ex)
        {
            Log.Error("初始化失败", ex);
            MessageBox.Show(
                "LightClipboard 初始化失败：\n" + ex.Message,
                "LightClipboard",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ThemeService.Apply(_host.SettingsService.Current.Theme);

        _window = new MainWindow(_host);
        MainWindow = _window;

        // 即使不显示窗口，也要先创建句柄，才能开始监听剪切板与注册热键
        new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();

        bool startHidden = HasFlag(args, "--startup") || HasFlag(args, "--hidden");
        if (!startHidden)
        {
            _window.ShowPanel();
        }

        if (HasFlag(args, "--demo"))
        {
            // 异步写入演示数据，避免阻塞 UI 线程
            _ = DemoData.SeedAsync(_host);
        }

        _host.RunStartupMaintenance();

        Log.Info($"LightClipboard 启动完成（{(startHidden ? "后台" : "面板已显示")}），数据目录: {AppPaths.Root}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _window?.AllowClose();
            _showRegistration?.Unregister(null);
            _showEvent?.Dispose();
            _host?.Dispose();

            if (_mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                    // 未持有互斥体时忽略
                }

                _mutex.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("退出清理时发生异常", ex);
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI 线程未处理异常", e.Exception);

        // 剪切板被占用、单个卡片渲染失败等不应让整个程序崩溃
        e.Handled = true;
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}
