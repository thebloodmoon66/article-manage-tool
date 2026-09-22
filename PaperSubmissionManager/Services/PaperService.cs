using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Data.Sqlite;
using PaperSubmissionManager.Models;

namespace PaperSubmissionManager.Services;

/// <summary>
/// 管理论文、附件、投稿历史和投稿纪要。
///
/// 附件的文件系统操作无法和 SQLite 组成真正的分布式事务，因此这里使用暂存与补偿：
/// 导入失败时删除已经复制的文件，删除失败时把预先移入回收区的文件恢复原位。
/// </summary>
public sealed class PaperService(DatabaseService database)
{
    public static IReadOnlyList<string> SubmissionStatuses { get; } = ["投稿中", "已投稿", "未投稿", "已拒稿", "返修中"];
    private static readonly HashSet<string> RiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".appref-ms", ".application", ".appx", ".appxbundle",
        ".bat", ".cmd", ".com", ".cpl", ".dll", ".exe", ".gadget",
        ".hta", ".inf", ".ins", ".iso", ".img", ".jar", ".js", ".jse",
        ".lnk", ".msc", ".msi", ".msp", ".mst", ".pif", ".ps1", ".psd1",
        ".psm1", ".reg", ".scr", ".sct", ".shb", ".sys", ".url", ".vbe",
        ".vbs", ".vhd", ".vhdx", ".ws", ".wsc", ".wsf", ".wsh",
        // 可包含自动运行宏的 Office 文件也应在打开前提示用户。
        ".docm", ".dotm", ".xlam", ".xlsm", ".xltm", ".pptm", ".potm",
        ".ppam", ".ppsm", ".sldm"
    };

    public long CreatePaper(
        string name,
        string notes = "",
        IEnumerable<PendingAttachment>? attachments = null)
    {
        var normalizedName = RequireText(name, "论文名称");
        var prepared = PrepareAttachments(attachments);
        var finalizedPaths = new List<string>();

        try
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            var now = DatabaseService.Now();
            long paperId;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO Papers(Name, Notes, CreatedAt, UpdatedAt)
                    VALUES($name, $notes, $now, $now)
                    RETURNING Id;
                    """;
                command.Parameters.AddWithValue("$name", normalizedName);
                command.Parameters.AddWithValue("$notes", notes?.Trim() ?? "");
                command.Parameters.AddWithValue("$now", now);
                paperId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            FinalizeAttachments(connection, transaction, paperId, prepared, finalizedPaths);
            transaction.Commit();
            return paperId;
        }
        catch
        {
            DeleteFilesBestEffort(finalizedPaths);
            throw;
        }
        finally
        {
            DeleteDirectoryBestEffort(prepared.StagingDirectory);
        }
    }

    public List<PaperRecord> ListPapers()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id, p.Name, p.Notes, p.CreatedAt,
                   (SELECT COUNT(*) FROM PaperAttachments a WHERE a.PaperId = p.Id),
                   (SELECT COUNT(*) FROM PaperSubmissions s WHERE s.PaperId = p.Id),
                   COALESCE((SELECT s.JournalName FROM PaperSubmissions s
                             WHERE s.PaperId=p.Id ORDER BY s.RecordedAt DESC, s.Id DESC LIMIT 1), ''),
                   COALESCE((SELECT s.CurrentStatus FROM PaperSubmissions s
                             WHERE s.PaperId=p.Id ORDER BY s.RecordedAt DESC, s.Id DESC LIMIT 1), '')
            FROM Papers p
            ORDER BY p.CreatedAt DESC, p.Id DESC;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<PaperRecord>();
        while (reader.Read()) rows.Add(ReadPaper(reader));
        return rows;
    }

    public PaperRecord? GetPaper(long paperId)
    {
        RequireId(paperId, "论文");
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id, p.Name, p.Notes, p.CreatedAt,
                   (SELECT COUNT(*) FROM PaperAttachments a WHERE a.PaperId = p.Id),
                   (SELECT COUNT(*) FROM PaperSubmissions s WHERE s.PaperId = p.Id),
                   COALESCE((SELECT s.JournalName FROM PaperSubmissions s
                             WHERE s.PaperId=p.Id ORDER BY s.RecordedAt DESC, s.Id DESC LIMIT 1), ''),
                   COALESCE((SELECT s.CurrentStatus FROM PaperSubmissions s
                             WHERE s.PaperId=p.Id ORDER BY s.RecordedAt DESC, s.Id DESC LIMIT 1), '')
            FROM Papers p
            WHERE p.Id = $id;
            """;
        command.Parameters.AddWithValue("$id", paperId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPaper(reader) : null;
    }

    public void UpdatePaper(long paperId, string name, string notes)
    {
        RequireId(paperId, "论文");
        var normalizedName = RequireText(name, "论文名称");
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Papers
            SET Name = $name, Notes = $notes, UpdatedAt = $now
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$name", normalizedName);
        command.Parameters.AddWithValue("$notes", notes?.Trim() ?? "");
        command.Parameters.AddWithValue("$now", DatabaseService.Now());
        command.Parameters.AddWithValue("$id", paperId);
        EnsureOneRow(command.ExecuteNonQuery(), "论文记录不存在或已被删除。");
    }

    public void UpdatePaperNotes(long paperId, string notes)
    {
        RequireId(paperId, "论文");
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Papers SET Notes = $notes, UpdatedAt = $now WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$notes", notes?.Trim() ?? "");
        command.Parameters.AddWithValue("$now", DatabaseService.Now());
        command.Parameters.AddWithValue("$id", paperId);
        EnsureOneRow(command.ExecuteNonQuery(), "论文记录不存在或已被删除。");
    }

    public void DeletePaper(long paperId)
    {
        RequireId(paperId, "论文");
        using var connection = database.OpenConnection();

        List<string> storedPaths;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT StoredPath FROM PaperAttachments WHERE PaperId = $id;";
            command.Parameters.AddWithValue("$id", paperId);
            using var reader = command.ExecuteReader();
            storedPaths = [];
            while (reader.Read()) storedPaths.Add(reader.GetString(0));
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM Papers WHERE Id = $id);";
            command.Parameters.AddWithValue("$id", paperId);
            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("论文记录不存在或已被删除。");
        }

        var quarantine = QuarantineFiles(storedPaths);
        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Papers WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", paperId);
            EnsureOneRow(command.ExecuteNonQuery(), "论文记录不存在或已被删除。");
            transaction.Commit();
        }
        catch
        {
            RestoreQuarantineBestEffort(quarantine);
            throw;
        }

        CompleteQuarantineBestEffort(quarantine);
    }

    public List<AttachmentRecord> AddAttachments(
        long paperId,
        IEnumerable<PendingAttachment> attachments)
    {
        RequireId(paperId, "论文");
        ArgumentNullException.ThrowIfNull(attachments);
        var prepared = PrepareAttachments(attachments);
        var finalizedPaths = new List<string>();

        try
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            EnsurePaperExists(connection, transaction, paperId);
            var rows = FinalizeAttachments(connection, transaction, paperId, prepared, finalizedPaths);
            transaction.Commit();
            return rows;
        }
        catch
        {
            DeleteFilesBestEffort(finalizedPaths);
            throw;
        }
        finally
        {
            DeleteDirectoryBestEffort(prepared.StagingDirectory);
        }
    }

    public List<AttachmentRecord> ListAttachments(long paperId)
    {
        RequireId(paperId, "论文");
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, PaperId, DisplayName, OriginalFileName, StoredPath, CreatedAt
            FROM PaperAttachments
            WHERE PaperId = $paper
            ORDER BY CreatedAt, Id;
            """;
        command.Parameters.AddWithValue("$paper", paperId);
        using var reader = command.ExecuteReader();
        var rows = new List<AttachmentRecord>();
        while (reader.Read()) rows.Add(ReadAttachment(reader));
        return rows;
    }

    public AttachmentRecord? GetAttachment(long attachmentId)
    {
        RequireId(attachmentId, "附件");
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, PaperId, DisplayName, OriginalFileName, StoredPath, CreatedAt
            FROM PaperAttachments WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", attachmentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAttachment(reader) : null;
    }

    public AttachmentRecord RenameAttachmentFile(long attachmentId, string newFileName)
    {
        RequireId(attachmentId, "附件");
        var requestedName = ValidateAttachmentFileName(newFileName);
        var attachment = GetAttachment(attachmentId)
            ?? throw new InvalidOperationException("附件记录不存在或已被删除。");
        var originalPath = ResolveAttachmentPath(attachment);
        var currentName = Path.GetFileName(originalPath);
        if (string.Equals(currentName, requestedName, StringComparison.Ordinal)) return attachment;

        var directory = Path.GetDirectoryName(originalPath)!;
        string finalPath;
        string? intermediatePath = null;
        if (string.Equals(currentName, requestedName, StringComparison.OrdinalIgnoreCase))
        {
            intermediatePath = Path.Combine(directory, $".rename-{Guid.NewGuid():N}.tmp");
            File.Move(originalPath, intermediatePath);
            finalPath = Path.Combine(directory, requestedName);
        }
        else
        {
            finalPath = GetAvailableAttachmentPath(directory, requestedName);
        }

        var sourceForMove = intermediatePath ?? originalPath;
        try
        {
            File.Move(sourceForMove, finalPath);
            var relative = Path.GetRelativePath(database.Paths.AttachmentRoot, finalPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE PaperAttachments SET OriginalFileName=$name,StoredPath=$stored WHERE Id=$id;";
            command.Parameters.AddWithValue("$name", Path.GetFileName(finalPath));
            command.Parameters.AddWithValue("$stored", relative);
            command.Parameters.AddWithValue("$id", attachmentId);
            EnsureOneRow(command.ExecuteNonQuery(), "附件记录不存在或已被删除。");
            return GetAttachment(attachmentId)!;
        }
        catch
        {
            try
            {
                if (File.Exists(finalPath) && !File.Exists(originalPath)) File.Move(finalPath, originalPath);
                else if (intermediatePath is not null && File.Exists(intermediatePath) && !File.Exists(originalPath)) File.Move(intermediatePath, originalPath);
            }
            catch { }
            throw;
        }
    }

    public void NormalizeLegacyAttachmentFileNames()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,PaperId,DisplayName,OriginalFileName,StoredPath,CreatedAt FROM PaperAttachments ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var legacy = new List<AttachmentRecord>();
        while (reader.Read())
        {
            var item = ReadAttachment(reader);
            var stem = Path.GetFileNameWithoutExtension(item.ActualFileName);
            if (stem.Length == 32 && stem.All(Uri.IsHexDigit) && !string.Equals(item.ActualFileName, item.OriginalFileName, StringComparison.OrdinalIgnoreCase)) legacy.Add(item);
        }
        reader.Close();
        foreach (var item in legacy)
        {
            try { RenameAttachmentFile(item.Id, item.OriginalFileName); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void DeleteAttachment(long attachmentId)
    {
        RequireId(attachmentId, "附件");
        using var connection = database.OpenConnection();
        string storedPath;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT StoredPath FROM PaperAttachments WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", attachmentId);
            storedPath = command.ExecuteScalar() as string
                ?? throw new InvalidOperationException("附件记录不存在或已被删除。");
        }

        var quarantine = QuarantineFiles([storedPath]);
        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM PaperAttachments WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", attachmentId);
            EnsureOneRow(command.ExecuteNonQuery(), "附件记录不存在或已被删除。");
            transaction.Commit();
        }
        catch
        {
            RestoreQuarantineBestEffort(quarantine);
            throw;
        }

        CompleteQuarantineBestEffort(quarantine);
    }

    public long AddSubmission(long paperId, string journalName, long? journalWorkspaceId = null)
    {
        RequireId(paperId, "论文");
        if (journalWorkspaceId is <= 0) throw new ArgumentOutOfRangeException(nameof(journalWorkspaceId));

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsurePaperExists(connection, transaction, paperId);

        string normalizedJournalName;
        if (journalWorkspaceId is { } workspaceId)
        {
            using var workspaceCommand = connection.CreateCommand();
            workspaceCommand.Transaction = transaction;
            workspaceCommand.CommandText = "SELECT JournalName FROM JournalWorkspaces WHERE Id = $id;";
            workspaceCommand.Parameters.AddWithValue("$id", workspaceId);
            normalizedJournalName = workspaceCommand.ExecuteScalar() as string
                ?? throw new InvalidOperationException("期刊投稿栏目不存在或已被删除。");
        }
        else
        {
            normalizedJournalName = RequireText(journalName, "投稿期刊名称");
        }

        var now = DatabaseService.Now();
        long submissionId;
        if (journalWorkspaceId is { } reusableWorkspaceId)
        {
            using var reusable = connection.CreateCommand();
            reusable.Transaction = transaction;
            reusable.CommandText = """
                SELECT s.Id
                FROM PaperSubmissions s
                WHERE s.PaperId=$paper AND s.JournalWorkspaceId=$workspace
                  AND s.CurrentStatus='未投稿'
                  AND NOT EXISTS(SELECT 1 FROM SubmissionNotes n WHERE n.PaperSubmissionId=s.Id)
                ORDER BY s.RecordedAt DESC,s.Id DESC LIMIT 1;
                """;
            reusable.Parameters.AddWithValue("$paper", paperId);
            reusable.Parameters.AddWithValue("$workspace", reusableWorkspaceId);
            if (reusable.ExecuteScalar() is { } existing)
            {
                submissionId = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE PaperSubmissions SET JournalName=$name,RecordedAt=$now WHERE Id=$id;";
                update.Parameters.AddWithValue("$name", normalizedJournalName.Trim());
                update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$id", submissionId);
                update.ExecuteNonQuery();
                transaction.Commit();
                return submissionId;
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO PaperSubmissions(PaperId, JournalWorkspaceId, JournalName, CurrentStatus, RecordedAt)
                VALUES($paper, $workspace, $journal, '未投稿', $now)
                RETURNING Id;
                """;
            command.Parameters.AddWithValue("$paper", paperId);
            command.Parameters.AddWithValue("$workspace", journalWorkspaceId is { } id ? id : DBNull.Value);
            command.Parameters.AddWithValue("$journal", normalizedJournalName.Trim());
            command.Parameters.AddWithValue("$now", now);
            submissionId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (journalWorkspaceId is { } linkedWorkspaceId)
        {
            using var linkCommand = connection.CreateCommand();
            linkCommand.Transaction = transaction;
            linkCommand.CommandText = """
                INSERT OR IGNORE INTO JournalWorkspacePapers(JournalWorkspaceId, PaperId, CreatedAt)
                VALUES($workspace, $paper, $now);
                """;
            linkCommand.Parameters.AddWithValue("$workspace", linkedWorkspaceId);
            linkCommand.Parameters.AddWithValue("$paper", paperId);
            linkCommand.Parameters.AddWithValue("$now", now);
            linkCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return submissionId;
    }

    public List<PaperSubmissionRecord> ListSubmissions(long paperId)
    {
        RequireId(paperId, "论文");
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.PaperId, s.JournalWorkspaceId, s.JournalName, s.RecordedAt, s.CurrentStatus,
                   (SELECT COUNT(*) FROM SubmissionNotes n
                    WHERE n.PaperSubmissionId = s.Id AND n.IsCurrent = 1)
            FROM PaperSubmissions s
            WHERE s.PaperId = $paper
            ORDER BY s.RecordedAt DESC, s.Id DESC;
            """;
        command.Parameters.AddWithValue("$paper", paperId);
        using var reader = command.ExecuteReader();
        var rows = new List<PaperSubmissionRecord>();
        while (reader.Read()) rows.Add(ReadSubmission(reader));
        return rows;
    }

    public PaperSubmissionRecord? GetSubmission(long submissionId)
    {
        RequireId(submissionId, "投稿历史");
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.PaperId, s.JournalWorkspaceId, s.JournalName, s.RecordedAt, s.CurrentStatus,
                   (SELECT COUNT(*) FROM SubmissionNotes n
                    WHERE n.PaperSubmissionId = s.Id AND n.IsCurrent = 1)
            FROM PaperSubmissions s
            WHERE s.Id = $id;
            """;
        command.Parameters.AddWithValue("$id", submissionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSubmission(reader) : null;
    }

    public void DeleteSubmission(long submissionId)
    {
        RequireId(submissionId, "投稿历史");
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        long paperId;
        long? workspaceId;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT PaperId,JournalWorkspaceId FROM PaperSubmissions WHERE Id=$id;";
            lookup.Parameters.AddWithValue("$id", submissionId);
            using var reader = lookup.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("投稿历史不存在或已被删除。");
            paperId = reader.GetInt64(0);
            workspaceId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM PaperSubmissions WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", submissionId);
            EnsureOneRow(command.ExecuteNonQuery(), "投稿历史不存在或已被删除。");
        }
        if (workspaceId is { } linkedWorkspaceId)
        {
            using var unlink = connection.CreateCommand();
            unlink.Transaction = transaction;
            unlink.CommandText = """
                DELETE FROM JournalWorkspacePapers
                WHERE JournalWorkspaceId=$workspace AND PaperId=$paper
                  AND NOT EXISTS(
                      SELECT 1 FROM PaperSubmissions s
                      WHERE s.JournalWorkspaceId=$workspace AND s.PaperId=$paper);
                """;
            unlink.Parameters.AddWithValue("$workspace", linkedWorkspaceId);
            unlink.Parameters.AddWithValue("$paper", paperId);
            unlink.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public long AddSubmissionNote(long submissionId, string status, string content)
    {
        RequireId(submissionId, "投稿历史");
        var normalizedStatus = RequireSubmissionStatus(status);
        var normalizedContent = RequireText(content, "投稿状态纪要");
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsureSubmissionExists(connection, transaction, submissionId);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SubmissionNotes(
                PaperSubmissionId, VersionGroupId, VersionNumber, Content, RecordedAt, IsCurrent)
            VALUES($submission, $group, 1, $content, $now, 1)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$submission", submissionId);
        command.Parameters.AddWithValue("$group", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$content", normalizedContent);
        command.Parameters.AddWithValue("$now", DatabaseService.Now());
        var noteId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        using (var updateStatus = connection.CreateCommand())
        {
            updateStatus.Transaction = transaction;
            updateStatus.CommandText = "UPDATE PaperSubmissions SET CurrentStatus=$status WHERE Id=$id;";
            updateStatus.Parameters.AddWithValue("$status", normalizedStatus);
            updateStatus.Parameters.AddWithValue("$id", submissionId);
            EnsureOneRow(updateStatus.ExecuteNonQuery(), "投稿历史不存在或已被删除。");
        }
        transaction.Commit();
        return noteId;
    }

    public void UpdateSubmissionStatus(long submissionId, string status)
    {
        RequireId(submissionId, "投稿历史");
        var normalizedStatus = RequireSubmissionStatus(status);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PaperSubmissions SET CurrentStatus=$status WHERE Id=$id;";
        command.Parameters.AddWithValue("$status", normalizedStatus);
        command.Parameters.AddWithValue("$id", submissionId);
        EnsureOneRow(command.ExecuteNonQuery(), "投稿历史不存在或已被删除。");
    }

    /// <summary>
    /// 编辑纪要不会覆盖旧内容，而是在同一个 VersionGroupId 下追加一个新版本。
    /// 传入该组任意一个版本的 Id 均可定位到当前版本。
    /// </summary>
    public long EditSubmissionNote(long noteId, string content)
    {
        RequireId(noteId, "投稿状态纪要");
        var normalizedContent = RequireText(content, "投稿状态纪要");
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        long submissionId;
        string versionGroupId;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT PaperSubmissionId, VersionGroupId
                FROM SubmissionNotes WHERE Id = $id;
                """;
            lookup.Parameters.AddWithValue("$id", noteId);
            using var reader = lookup.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("投稿状态纪要不存在或已被删除。");
            submissionId = reader.GetInt64(0);
            versionGroupId = reader.GetString(1);
        }

        int nextVersion;
        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = """
                SELECT COALESCE(MAX(VersionNumber), 0),
                       COALESCE(SUM(CASE WHEN IsCurrent = 1 THEN 1 ELSE 0 END), 0)
                FROM SubmissionNotes
                WHERE VersionGroupId = $group;
                """;
            versionCommand.Parameters.AddWithValue("$group", versionGroupId);
            using var reader = versionCommand.ExecuteReader();
            reader.Read();
            nextVersion = reader.GetInt32(0) + 1;
            if (reader.GetInt32(1) == 0)
                throw new InvalidOperationException("已归档的投稿状态纪要不能直接编辑。");
        }

        using (var retire = connection.CreateCommand())
        {
            retire.Transaction = transaction;
            retire.CommandText = "UPDATE SubmissionNotes SET IsCurrent = 0 WHERE VersionGroupId = $group;";
            retire.Parameters.AddWithValue("$group", versionGroupId);
            retire.ExecuteNonQuery();
        }

        long newNoteId;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO SubmissionNotes(
                    PaperSubmissionId, VersionGroupId, VersionNumber, Content, RecordedAt, IsCurrent)
                VALUES($submission, $group, $version, $content, $now, 1)
                RETURNING Id;
                """;
            insert.Parameters.AddWithValue("$submission", submissionId);
            insert.Parameters.AddWithValue("$group", versionGroupId);
            insert.Parameters.AddWithValue("$version", nextVersion);
            insert.Parameters.AddWithValue("$content", normalizedContent);
            insert.Parameters.AddWithValue("$now", DatabaseService.Now());
            newNoteId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        transaction.Commit();
        return newNoteId;
    }

    /// <summary>
    /// 返回纪要的所有历史版本。includeArchived=false 时仍会返回未归档纪要组的旧版本，
    /// 但会排除已经整组归档的纪要。
    /// </summary>
    public List<SubmissionNoteRecord> ListSubmissionNotes(
        long submissionId,
        bool includeArchived = true)
    {
        RequireId(submissionId, "投稿历史");
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.Id, n.PaperSubmissionId, n.VersionGroupId, n.VersionNumber,
                   n.Content, n.RecordedAt, n.IsCurrent
            FROM SubmissionNotes n
            WHERE n.PaperSubmissionId = $submission
              AND ($includeArchived = 1 OR EXISTS(
                    SELECT 1 FROM SubmissionNotes current
                    WHERE current.VersionGroupId = n.VersionGroupId
                      AND current.IsCurrent = 1))
            ORDER BY n.RecordedAt DESC, n.Id DESC;
            """;
        command.Parameters.AddWithValue("$submission", submissionId);
        command.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);
        using var reader = command.ExecuteReader();
        var rows = new List<SubmissionNoteRecord>();
        while (reader.Read()) rows.Add(ReadSubmissionNote(reader));
        return rows;
    }

    /// <summary>
    /// 归档一组纪要。历史版本仍保留在数据库中，可通过 includeArchived=true 查看。
    /// </summary>
    public void ArchiveSubmissionNote(string versionGroupId)
    {
        var normalizedGroupId = RequireText(versionGroupId, "纪要版本组");
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SubmissionNotes SET IsCurrent = 0 WHERE VersionGroupId = $group;
            """;
        command.Parameters.AddWithValue("$group", normalizedGroupId);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("投稿状态纪要不存在或已被删除。");
    }

    /// <summary>
    /// 永久删除一组纪要及其全部版本。
    /// </summary>
    public void DeleteSubmissionNoteHistory(string versionGroupId)
    {
        var normalizedGroupId = RequireText(versionGroupId, "纪要版本组");
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SubmissionNotes WHERE VersionGroupId = $group;";
        command.Parameters.AddWithValue("$group", normalizedGroupId);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("投稿状态纪要不存在或已被删除。");
    }

    public string ResolveAttachmentPath(long attachmentId)
    {
        var attachment = GetAttachment(attachmentId)
            ?? throw new InvalidOperationException("附件记录不存在或已被删除。");
        return ResolveAttachmentPath(attachment.StoredPath);
    }

    public string ResolveAttachmentPath(AttachmentRecord attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return ResolveAttachmentPath(attachment.StoredPath);
    }

    /// <summary>
    /// 把数据库中的相对路径解析为附件根目录内的绝对路径。
    /// 绝对路径、父目录跳转、Windows 非法文件名和现存重解析点都会被拒绝。
    /// </summary>
    public string ResolveAttachmentPath(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || !string.Equals(storedPath, storedPath.Trim(), StringComparison.Ordinal))
            throw new SecurityException("附件存储路径无效。");
        if (Path.IsPathRooted(storedPath))
            throw new SecurityException("附件存储路径必须是受控目录内的相对路径。");

        var components = storedPath.Split(['\\', '/'], StringSplitOptions.None);
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        if (components.Length == 0 || components.Any(component =>
                component.Length == 0 ||
                component is "." or ".." ||
                component.EndsWith(' ') ||
                component.EndsWith('.') ||
                component.IndexOfAny(invalidFileNameChars) >= 0))
            throw new SecurityException("附件存储路径包含不安全的路径片段。");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(database.Paths.AttachmentRoot));
        var candidate = Path.GetFullPath(Path.Combine([root, .. components]));
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new SecurityException("附件路径越出了受控附件目录。");

        EnsureNoReparsePoints(root, components);
        if (!File.Exists(candidate))
            throw new FileNotFoundException("受控附件文件不存在，可能已被移动或删除。", candidate);
        return candidate;
    }

    public bool IsRiskyExtension(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) return false;
        var normalized = fileNameOrPath.Trim().TrimEnd(' ', '.');
        if (normalized.Contains(':') && !Path.IsPathRooted(normalized)) return true;
        var extension = Path.GetExtension(normalized);
        return RiskyExtensions.Contains(extension);
    }

    private static PaperRecord ReadPaper(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Notes = reader.GetString(2),
        CreatedAt = reader.GetString(3),
        AttachmentCount = Convert.ToInt32(reader.GetInt64(4), CultureInfo.InvariantCulture),
        SubmissionCount = Convert.ToInt32(reader.GetInt64(5), CultureInfo.InvariantCulture),
        LatestJournalName = reader.FieldCount > 6 ? reader.GetString(6) : "",
        LatestSubmissionStatus = reader.FieldCount > 7 ? reader.GetString(7) : ""
    };

    private static AttachmentRecord ReadAttachment(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        PaperId = reader.GetInt64(1),
        DisplayName = reader.GetString(2),
        OriginalFileName = reader.GetString(3),
        StoredPath = reader.GetString(4),
        CreatedAt = reader.GetString(5)
    };

    private static PaperSubmissionRecord ReadSubmission(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        PaperId = reader.GetInt64(1),
        JournalWorkspaceId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
        JournalName = reader.GetString(3),
        RecordedAt = reader.GetString(4),
        CurrentStatus = reader.GetString(5),
        NoteCount = Convert.ToInt32(reader.GetInt64(6), CultureInfo.InvariantCulture)
    };

    private static SubmissionNoteRecord ReadSubmissionNote(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        PaperSubmissionId = reader.GetInt64(1),
        VersionGroupId = reader.GetString(2),
        VersionNumber = reader.GetInt32(3),
        Content = reader.GetString(4),
        RecordedAt = reader.GetString(5),
        IsCurrent = reader.GetInt32(6) == 1
    };

    private PreparedAttachmentBatch PrepareAttachments(IEnumerable<PendingAttachment>? pendingAttachments)
    {
        if (pendingAttachments is null) return PreparedAttachmentBatch.Empty;
        var pending = pendingAttachments.ToList();
        if (pending.Count == 0) return PreparedAttachmentBatch.Empty;

        var sources = new List<AttachmentSource>(pending.Count);
        foreach (var item in pending)
        {
            if (item is null) throw new ArgumentException("附件列表中不能包含空记录。", nameof(pendingAttachments));
            var displayName = RequireText(item.DisplayName, "附件名称");
            if (string.IsNullOrWhiteSpace(item.SourcePath))
                throw new ArgumentException("附件来源路径不能为空。", nameof(pendingAttachments));

            var sourcePath = Path.GetFullPath(item.SourcePath);
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("找不到要导入的附件。", sourcePath);
            var originalFileName = Path.GetFileName(sourcePath);
            var extension = Path.GetExtension(originalFileName);
            if (extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || extension.Length > 180)
                throw new InvalidOperationException($"附件“{originalFileName}”的扩展名无法安全保留。");
            sources.Add(new AttachmentSource(displayName, sourcePath, originalFileName, extension));
        }

        var stagingDirectory = Path.Combine(
            database.Paths.AttachmentRoot,
            ".staging",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var stagedItems = new List<StagedAttachment>(sources.Count);
        try
        {
            foreach (var source in sources)
            {
                var stagedFileName = Guid.NewGuid().ToString("N") + source.Extension;
                var stagedPath = Path.Combine(stagingDirectory, stagedFileName);
                File.Copy(source.SourcePath, stagedPath, overwrite: false);
                stagedItems.Add(new StagedAttachment(
                    source.DisplayName,
                    source.OriginalFileName,
                    stagedPath));
            }

            return new PreparedAttachmentBatch(stagingDirectory, stagedItems);
        }
        catch
        {
            DeleteDirectoryBestEffort(stagingDirectory);
            throw;
        }
    }

    private List<AttachmentRecord> FinalizeAttachments(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long paperId,
        PreparedAttachmentBatch prepared,
        List<string> finalizedPaths)
    {
        if (prepared.Items.Count == 0) return [];
        var paperDirectory = Path.Combine(database.Paths.AttachmentRoot, paperId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(paperDirectory);
        var records = new List<AttachmentRecord>(prepared.Items.Count);

        foreach (var item in prepared.Items)
        {
            var finalPath = GetAvailableAttachmentPath(paperDirectory, item.OriginalFileName);
            File.Move(item.StagedPath, finalPath);
            finalizedPaths.Add(finalPath);

            var relativePath = Path.GetRelativePath(database.Paths.AttachmentRoot, finalPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var now = DatabaseService.Now();
            long attachmentId;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO PaperAttachments(
                        PaperId, DisplayName, OriginalFileName, StoredPath, CreatedAt)
                    VALUES($paper, $display, $original, $stored, $now)
                    RETURNING Id;
                    """;
                command.Parameters.AddWithValue("$paper", paperId);
                command.Parameters.AddWithValue("$display", item.DisplayName);
                command.Parameters.AddWithValue("$original", item.OriginalFileName);
                command.Parameters.AddWithValue("$stored", relativePath);
                command.Parameters.AddWithValue("$now", now);
                attachmentId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            records.Add(new AttachmentRecord
            {
                Id = attachmentId,
                PaperId = paperId,
                DisplayName = item.DisplayName,
                OriginalFileName = item.OriginalFileName,
                StoredPath = relativePath,
                CreatedAt = now
            });
        }

        return records;
    }

    internal static string GetAvailableAttachmentPath(string directory, string originalFileName)
    {
        var safeFileName = ValidateAttachmentFileName(originalFileName);

        var candidate = Path.Combine(directory, safeFileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(safeFileName);
        var extension = Path.GetExtension(safeFileName);
        for (var number = 2; number < int.MaxValue; number++)
        {
            candidate = Path.Combine(directory, $"{stem} ({number}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("无法为附件生成不重复的保存文件名。");
    }

    private static string ValidateAttachmentFileName(string? fileName)
    {
        var name = fileName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith(' ') || name.EndsWith('.') || name.Length > 240)
            throw new InvalidOperationException("文件名无效。请只输入文件名，不要包含文件夹路径或 Windows 禁止字符。");
        return name;
    }

    private QuarantineBatch QuarantineFiles(IEnumerable<string> storedPaths)
    {
        string? quarantineDirectory = null;
        var moved = new List<QuarantinedFile>();
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var storedPath in storedPaths)
            {
                if (!TryResolveAttachmentPath(storedPath, out var originalPath) ||
                    !seen.Add(originalPath) || !File.Exists(originalPath)) continue;

                quarantineDirectory ??= Path.Combine(
                    database.Paths.AttachmentRoot,
                    ".trash",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(quarantineDirectory);
                var quarantinePath = Path.Combine(
                    quarantineDirectory,
                    Guid.NewGuid().ToString("N") + Path.GetExtension(originalPath));
                File.Move(originalPath, quarantinePath);
                moved.Add(new QuarantinedFile(originalPath, quarantinePath));
            }

            return new QuarantineBatch(quarantineDirectory, moved);
        }
        catch
        {
            RestoreQuarantineBestEffort(new QuarantineBatch(quarantineDirectory, moved));
            throw;
        }
    }

    private bool TryResolveAttachmentPath(string storedPath, out string fullPath)
    {
        try
        {
            fullPath = ResolveAttachmentPath(storedPath);
            return true;
        }
        catch (Exception exception) when (exception is
            SecurityException or ArgumentException or NotSupportedException or IOException)
        {
            fullPath = "";
            return false;
        }
    }

    private static void EnsureNoReparsePoints(string root, IReadOnlyList<string> components)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("受控附件目录不存在。");
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException("受控附件目录不能是符号链接或目录联接。");

        foreach (var component in components)
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new SecurityException("附件路径不能经过符号链接或目录联接。");
        }
    }

    private static void EnsurePaperExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long paperId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Papers WHERE Id = $id);";
        command.Parameters.AddWithValue("$id", paperId);
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("论文记录不存在或已被删除。");
    }

    private static void EnsureSubmissionExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long submissionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM PaperSubmissions WHERE Id = $id);";
        command.Parameters.AddWithValue("$id", submissionId);
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("投稿历史不存在或已被删除。");
    }

    private static string RequireText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{fieldName}不能为空。", fieldName);
        return value.Trim();
    }

    private static string RequireSubmissionStatus(string? value)
    {
        var status = RequireText(value, "投稿状态");
        if (!SubmissionStatuses.Contains(status, StringComparer.Ordinal))
            throw new ArgumentException("投稿状态必须是：投稿中、已投稿、未投稿、已拒稿或返修中。", nameof(value));
        return status;
    }

    private static void RequireId(long value, string entityName)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(entityName, $"{entityName}编号必须大于零。");
    }

    private static void EnsureOneRow(int affectedRows, string message)
    {
        if (affectedRows != 1) throw new InvalidOperationException(message);
    }

    private static void DeleteFilesBestEffort(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch
            {
                // 数据库事务已经回滚；残留的 GUID 文件不会覆盖用户原文件，可稍后清理。
            }
        }
    }

    private static void RestoreQuarantineBestEffort(QuarantineBatch quarantine)
    {
        foreach (var item in quarantine.Files.AsEnumerable().Reverse())
        {
            try
            {
                if (!File.Exists(item.QuarantinePath) || File.Exists(item.OriginalPath)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                File.Move(item.QuarantinePath, item.OriginalPath);
            }
            catch
            {
                // 保留回收区文件优于覆盖潜在的新文件。
            }
        }

        // 如果有文件恢复失败则保留回收区副本，绝不能为了清理目录而再次删除数据。
        DeleteEmptyDirectoryBestEffort(quarantine.Directory);
    }

    private static void CompleteQuarantineBestEffort(QuarantineBatch quarantine) =>
        DeleteDirectoryBestEffort(quarantine.Directory);

    private static void DeleteDirectoryBestEffort(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 最终清理失败不应把已经提交成功的数据库操作报告为失败。
        }
    }

    private static void DeleteEmptyDirectoryBestEffort(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch
        {
            // 回收区内仍有文件时必须保留，供人工恢复。
        }
    }

    private sealed record AttachmentSource(
        string DisplayName,
        string SourcePath,
        string OriginalFileName,
        string Extension);

    private sealed record StagedAttachment(
        string DisplayName,
        string OriginalFileName,
        string StagedPath);

    private sealed record QuarantinedFile(string OriginalPath, string QuarantinePath);

    private sealed record QuarantineBatch(string? Directory, IReadOnlyList<QuarantinedFile> Files);

    private sealed record PreparedAttachmentBatch(
        string? StagingDirectory,
        IReadOnlyList<StagedAttachment> Items)
    {
        public static PreparedAttachmentBatch Empty { get; } = new(null, []);
    }
}
