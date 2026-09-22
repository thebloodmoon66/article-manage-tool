using System.IO;
using Microsoft.Data.Sqlite;
using PaperSubmissionManager.Models;

namespace PaperSubmissionManager.Services;

public sealed class BackupService(DatabaseService database)
{
    public string? CreateSnapshot(string reason)
    {
        if (!File.Exists(database.Paths.DatabasePath)) return null;
        var safeReason = string.Concat(reason.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var tempPath = Path.Combine(database.Paths.BackupRoot, $"pending-{DateTime.Now:yyyyMMdd-HHmmssfff}-{safeReason}.db");
        using var source = database.OpenConnection();
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempPath, Pooling = false }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
        using var check = destination.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(check.ExecuteScalar());
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            destination.Close();
            File.Delete(tempPath);
            throw new InvalidOperationException("数据库备份完整性校验失败，操作已取消。");
        }
        return tempPath;
    }

    public void CommitSnapshot(string? tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath) || !File.Exists(tempPath)) return;
        var final = Path.Combine(database.Paths.BackupRoot, Path.GetFileName(tempPath).Replace("pending-", "backup-"));
        File.Move(tempPath, final);
        var backups = Directory.GetFiles(database.Paths.BackupRoot, "backup-*.db")
            .Select(path => new FileInfo(path)).OrderByDescending(x => x.LastWriteTimeUtc).ToList();
        foreach (var old in backups.Skip(3)) old.Delete();
    }

    public void DiscardSnapshot(string? tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath) || !File.Exists(tempPath)) return;
        try { File.Delete(tempPath); }
        catch (IOException) { /* 保留待清理快照，不能覆盖触发回滚的原始异常。 */ }
        catch (UnauthorizedAccessException) { /* 同上。 */ }
    }

    public List<BackupRecord> List() => Directory.GetFiles(database.Paths.BackupRoot, "backup-*.db")
        .Select(path => new FileInfo(path))
        .OrderByDescending(file => file.LastWriteTimeUtc)
        .Select(file => new BackupRecord { Path = file.FullName, Name = file.Name, ModifiedAt = file.LastWriteTime, Length = file.Length }).ToList();
}
