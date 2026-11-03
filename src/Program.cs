using System.Windows;

namespace MinecraftSessionTool;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new MainWindow(args.FirstOrDefault(arg => !arg.StartsWith("-"))));
    }
}
