using System.IO;

namespace PaperSubmissionManager.Services;

public sealed class AppPaths
{
    public AppPaths(string selectedRoot)
    {
        if (string.IsNullOrWhiteSpace(selectedRoot))
            throw new ArgumentException("必须先选择数据文件夹。", nameof(selectedRoot));

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedRoot.Trim()));
        if (File.Exists(Root))
            throw new IOException("所选路径是文件，必须选择文件夹。");

        DatabasePath = Path.Combine(Root, "paper-submissions.db");
        AttachmentRoot = Path.Combine(Root, "attachments");
        BackupRoot = Path.Combine(Root, "backups");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AttachmentRoot);
        Directory.CreateDirectory(BackupRoot);
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public string AttachmentRoot { get; }
    public string BackupRoot { get; }
}
