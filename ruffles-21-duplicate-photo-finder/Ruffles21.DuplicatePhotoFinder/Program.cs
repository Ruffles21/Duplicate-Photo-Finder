using System;
using System.IO;
using System.Windows;

namespace Ruffles21.DuplicatePhotoFinder;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            string log = Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath)!, "last-error.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.WriteAllText(log, e.Exception.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            MessageBox.Show("The app encountered an unexpected error and needs to restart.\n\n" + e.Exception.Message + "\n\nDiagnostic log: " + log, "ruffles_21’s Duplicate Photo Finder");
            e.Handled = true;
            app.Shutdown(1);
        };
        app.Run(new MainWindow());
    }
}
