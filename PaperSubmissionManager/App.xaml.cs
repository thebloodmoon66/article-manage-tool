using System.Windows;
using PaperSubmissionManager.Services;

namespace PaperSubmissionManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var argumentPath = ReadDataDirectoryArgument(e.Args);
        var storedPath = argumentPath is null ? DataLocationService.Load() : null;
        var loadedFromPortableConfig = storedPath is not null;
        var dataRoot = argumentPath ?? storedPath;
        if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = DataLocationService.Choose();
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            Shutdown();
            return;
        }

        try
        {
            var window = new MainWindow(dataRoot);
            DataLocationService.Save(dataRoot);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            if (loadedFromPortableConfig) DataLocationService.Clear();
            MessageBox.Show($"软件初始化失败：\n{ex.Message}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static string? ReadDataDirectoryArgument(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
                return args[i]["--data-dir=".Length..].Trim('"');

            if (args[i].Equals("--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                return args[i + 1].Trim('"');
        }

        return null;
    }

}


