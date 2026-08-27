using System;
using System.IO;
using System.Windows;

namespace SifuMovesetEditor;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                File.AppendAllText("error.log", $"[{DateTime.Now:HH:mm:ss}] [FATAL] {ex}\n");
        };
        DispatcherUnhandledException += (_, args) =>
        {
            File.AppendAllText("error.log", $"[{DateTime.Now:HH:mm:ss}] [UI] {args.Exception}\n");
            args.Handled = true;
        };
    }
}

