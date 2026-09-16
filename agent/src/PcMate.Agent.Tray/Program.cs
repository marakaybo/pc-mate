using System.Runtime.Versioning;
using System.Windows.Forms;

namespace PcMate.Agent.Tray;

[SupportedOSPlatform("windows")]
public static class Program
{
    private const string MutexName = "Global\\PcMate.Tray.SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        // Помощник запускается при входе в систему — вторая копия не нужна.
        using var mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("PC MATE уже запущен — значок находится в области уведомлений.",
                "PC MATE", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        var firstRun = args.Contains("--first-run");
        var showWizard = firstRun || args.Contains("--wizard");
        Application.Run(new TrayApplicationContext(showPairingOnStart: firstRun, showWizardOnStart: showWizard));
    }

    private static void ReportCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcMate", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "tray-errors.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
        }
        catch
        {
            // Записать не удалось — показываем пользователю то, что есть.
        }

        MessageBox.Show("Помощник PC MATE столкнулся с ошибкой:\n\n" + ex.Message,
            "PC MATE", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
