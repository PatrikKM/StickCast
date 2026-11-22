using System.Threading.Tasks;
using System.Windows;

namespace StickCast;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var ex = args.Exception;
            while (ex.InnerException != null) ex = ex.InnerException;
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StickCast");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {args.Exception}\r\n\r\n");
            }
            catch { }
            MessageBox.Show(ex.Message, "StickCast – chyba", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
        };

        base.OnStartup(e);
    }
}
