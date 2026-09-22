using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace PaperSubmissionManager.Services;

public static class DataLocationService
{
    private const string RelativePrefix = "relative:";
    public static string ConfigFilePath => Path.Combine(AppContext.BaseDirectory, "数据目录位置.txt");

    public static string? Load(string? configFilePath = null)
    {
        var file = configFilePath ?? ConfigFilePath;
        try
        {
            if (!File.Exists(file)) return null;
            var value = File.ReadAllText(file, Encoding.UTF8).Trim();
            if (value.Length == 0) return null;
            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(file))!;
            var configuredPath = value.StartsWith(RelativePrefix, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(configDirectory, value[RelativePrefix.Length..])
                : value;
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredPath));
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static void Save(string dataRoot, string? configFilePath = null)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var file = configFilePath ?? ConfigFilePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(file))
            ?? throw new InvalidOperationException("无法确定便携配置文件目录。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(file)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var relativePath = Path.GetRelativePath(directory, fullPath);
            var staysInsidePortableFolder = relativePath == "." ||
                (!Path.IsPathRooted(relativePath) && relativePath != ".." &&
                 !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            var storedValue = staysInsidePortableFolder ? RelativePrefix + relativePath : fullPath;
            File.WriteAllText(temporary, storedValue, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Clear(string? configFilePath = null)
    {
        try
        {
            var file = configFilePath ?? ConfigFilePath;
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 配置清理失败不应覆盖真正的数据库启动异常。
        }
    }

    public static string? Choose(Window? owner = null, string? currentPath = null)
    {
        var initialDirectory = !string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath)
            ? currentPath
            : AppContext.BaseDirectory;
        var dialog = new OpenFolderDialog
        {
            Title = "选择数据文件夹（数据库、附件和备份都会保存在这里）",
            InitialDirectory = initialDirectory,
            Multiselect = false
        };
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return result == true ? Path.GetFullPath(dialog.FolderName) : null;
    }
}
