using System.Windows;

namespace MinecraftSessionTool;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--apply-update")
        {
            Environment.ExitCode = AppUpdater.RunInstaller(args);
            return;
        }
        string? initialFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--cleanup-update" && i + 1 < args.Length)
                _ = AppUpdater.CleanupAsync(args[++i]);
            else if (!args[i].StartsWith("-") && initialFile == null)
                initialFile = args[i];
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow(initialFile);
        app.DispatcherUnhandledException += (_, e) => window.RecordUnhandledException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception) window.RecordUnhandledException(exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => window.RecordUnhandledException(e.Exception);
        app.Exit += (_, _) => window.RecordShutdown();
        if (args.Contains("--updated")) window.StartupUpdateMessage = "Đã cập nhật thành công. Chào mừng bạn quay lại ♡";
        if (args.Contains("--update-failed")) window.StartupUpdateMessage = "Chưa cài được bản cập nhật. App đã mở lại bản trước đó; bạn có thể thử lại sau.";
        app.Run(window);
    }
}

