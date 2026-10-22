using System;
using System.Windows;

namespace SifuMovesetEditor;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ErrorLog.Write("FATAL", ex);
        };
        DispatcherUnhandledException += (_, args) =>
        {
            // Routed through ErrorLog so crash lines land in the rotating, pruned
            // log file instead of a bare error.log in the working directory.
            ErrorLog.Write("UI", args.Exception);
            args.Handled = true;
        };
    }
}

