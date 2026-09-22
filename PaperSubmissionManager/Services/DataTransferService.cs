using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PaperSubmissionManager.Services;

public sealed class DataTransferService(DatabaseService database, BackupService backups, PaperService papers)
{
    private const int PackageVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public TransferResult Export(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("请选择导出文件。", nameof(destinationPath));
        destinationPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var package = ReadPackageFromDatabase();

        try
        {
            using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var paper in package.Papers)
            foreach (var attachment in paper.Attachments)
            {
                var record = papers.GetAttachment(attachment.SourceId)
                    ?? throw new InvalidOperationException($"附件“{attachment.DisplayName}”已不存在，导出已取消。");
                var sourcePath = papers.ResolveAttachmentPath(record);
                var entry = archive.CreateEntry(attachment.PackageEntry, CompressionLevel.Optimal);
                using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = entry.Open();
                input.CopyTo(output);
            }

            var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using var manifestStream = manifest.Open();
            JsonSerializer.Serialize(manifestStream, package, JsonOptions);
        }
        catch
        {
            try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
            throw;
        }

        return new TransferResult(package.Papers.Count, package.Workspaces.Count, package.Authors.Count, package.Papers.Sum(x => x.Attachments.Count));
    }

    public TransferResult Import(string packagePath)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("找不到要导入的数据包。", packagePath);
        using var file = new FileStream(Path.GetFullPath(packagePath), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("数据包缺少 manifest.json。");
        if (manifestEntry.Length > 50 * 1024 * 1024) throw new InvalidDataException("数据包清单过大。");
        TransferPackage package;
        using (var manifestStream = manifestEntry.Open())
            package = JsonSerializer.Deserialize<TransferPackage>(manifestStream, JsonOptions) ?? throw new InvalidDataException("数据包清单无效。");
        ValidatePackage(package, archive);

        var snapshot = backups.CreateSnapshot("导入业务数据");
        var createdFiles = new List<string>();
        var supersededFiles = new List<string>();
        try
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var authorMap = ImportAuthors(connection, transaction, package);
            var paperMap = ImportPapers(connection, transaction, package, archive, createdFiles, supersededFiles);
            var workspaceMap = ImportWorkspaces(connection, transaction, package);
            ImportSubmissions(connection, transaction, package, paperMap, workspaceMap);
            ImportWorkspaceLinks(connection, transaction, package, paperMap, workspaceMap);
            transaction.Commit();
            backups.CommitSnapshot(snapshot);
            MoveSupersededFilesToTrash(supersededFiles);
            _ = authorMap;
            return new TransferResult(package.Papers.Count, package.Workspaces.Count, package.Authors.Count, package.Papers.Sum(x => x.Attachments.Count));
        }
        catch
        {
            backups.DiscardSnapshot(snapshot);
            foreach (var path in createdFiles)
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            throw;
        }
    }

    private TransferPackage ReadPackageFromDatabase()
    {
        var package = new TransferPackage { Version = PackageVersion, ExportedAt = DatabaseService.Now() };
        using var connection = database.OpenConnection();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id,Name,Notes,CreatedAt FROM Papers ORDER BY Id;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) package.Papers.Add(new TransferPaper { SourceId = reader.GetInt64(0), Name = reader.GetString(1), Notes = reader.GetString(2), CreatedAt = reader.GetString(3) });
        }
        foreach (var paper in package.Papers)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id,DisplayName,OriginalFileName,CreatedAt FROM PaperAttachments WHERE PaperId=$paper ORDER BY Id;";
                command.Parameters.AddWithValue("$paper", paper.SourceId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    paper.Attachments.Add(new TransferAttachment { SourceId = id, DisplayName = reader.GetString(1), OriginalFileName = reader.GetString(2), CreatedAt = reader.GetString(3), PackageEntry = $"attachments/{paper.SourceId}/{id}{SafeExtension(reader.GetString(2))}" });
                }
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT s.Id,s.JournalName,s.CurrentStatus,s.RecordedAt,w.JournalName
                    FROM PaperSubmissions s LEFT JOIN JournalWorkspaces w ON w.Id=s.JournalWorkspaceId
                    WHERE s.PaperId=$paper ORDER BY s.RecordedAt DESC,s.Id DESC;
                    """;
                command.Parameters.AddWithValue("$paper", paper.SourceId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) paper.Submissions.Add(new TransferSubmission { SourceId = reader.GetInt64(0), JournalName = reader.GetString(1), CurrentStatus = reader.GetString(2), RecordedAt = reader.GetString(3), WorkspaceName = reader.IsDBNull(4) ? "" : reader.GetString(4) });
            }
            foreach (var submission in paper.Submissions)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT VersionGroupId,VersionNumber,Content,RecordedAt,IsCurrent FROM SubmissionNotes WHERE PaperSubmissionId=$id ORDER BY RecordedAt,Id;";
                command.Parameters.AddWithValue("$id", submission.SourceId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) submission.Notes.Add(new TransferNote { VersionGroupId = reader.GetString(0), VersionNumber = reader.GetInt32(1), Content = reader.GetString(2), RecordedAt = reader.GetString(3), IsCurrent = reader.GetInt32(4) == 1 });
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id,Name,CreatedAt FROM Authors ORDER BY Id;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) package.Authors.Add(new TransferAuthor { SourceId = reader.GetInt64(0), Name = reader.GetString(1), CreatedAt = reader.GetString(2) });
        }
        foreach (var author in package.Authors)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Email FROM AuthorEmails WHERE AuthorId=$id ORDER BY Id;";
            command.Parameters.AddWithValue("$id", author.SourceId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) author.Emails.Add(reader.GetString(0));
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id,JournalName,SubmissionLink,CreatedAt,UpdatedAt FROM JournalWorkspaces ORDER BY Id;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) package.Workspaces.Add(new TransferWorkspace { SourceId = reader.GetInt64(0), JournalName = reader.GetString(1), SubmissionLink = reader.GetString(2), CreatedAt = reader.GetString(3), UpdatedAt = reader.GetString(4) });
        }
        foreach (var workspace in package.Workspaces)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT Label,AccountName,PasswordCipher,CreatedAt,UpdatedAt
                    FROM JournalAccounts
                    WHERE JournalWorkspaceId=$id
                      AND NOT (Label='投稿账号' AND trim(AccountName)='' AND PasswordCipher='')
                    ORDER BY Id;
                    """;
                command.Parameters.AddWithValue("$id", workspace.SourceId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) workspace.Accounts.Add(new TransferAccount { Label = reader.GetString(0), AccountName = reader.GetString(1), Password = reader.GetString(2), CreatedAt = reader.GetString(3), UpdatedAt = reader.GetString(4) });
            }
            using var link = connection.CreateCommand();
            link.CommandText = "SELECT PaperId FROM JournalWorkspacePapers WHERE JournalWorkspaceId=$id ORDER BY PaperId;";
            link.Parameters.AddWithValue("$id", workspace.SourceId);
            using var linkReader = link.ExecuteReader();
            while (linkReader.Read()) workspace.LinkedPaperSourceIds.Add(linkReader.GetInt64(0));
        }
        return package;
    }

    private static Dictionary<long, long> ImportAuthors(SqliteConnection connection, SqliteTransaction transaction, TransferPackage package)
    {
        var map = new Dictionary<long, long>();
        var used = new HashSet<long>();
        foreach (var author in package.Authors)
        {
            var name = Required(author.Name, "作者名称");
            var id = FindUnusedId(connection, transaction, "Authors", "Name", name, used);
            if (id is null)
            {
                using var insert = Cmd(connection, transaction, "INSERT INTO Authors(Name,CreatedAt,UpdatedAt) VALUES($name,$created,$now) RETURNING Id;");
                insert.Parameters.AddWithValue("$name", name); insert.Parameters.AddWithValue("$created", ValidTime(author.CreatedAt)); insert.Parameters.AddWithValue("$now", DatabaseService.Now());
                id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            else
            {
                using var update = Cmd(connection, transaction, "UPDATE Authors SET Name=$name,CreatedAt=$created,UpdatedAt=$now WHERE Id=$id;");
                update.Parameters.AddWithValue("$name", name); update.Parameters.AddWithValue("$created", ValidTime(author.CreatedAt)); update.Parameters.AddWithValue("$now", DatabaseService.Now()); update.Parameters.AddWithValue("$id", id.Value); update.ExecuteNonQuery();
            }
            used.Add(id.Value); map[author.SourceId] = id.Value;
            using (var delete = Cmd(connection, transaction, "DELETE FROM AuthorEmails WHERE AuthorId=$id;")) { delete.Parameters.AddWithValue("$id", id.Value); delete.ExecuteNonQuery(); }
            foreach (var email in author.Emails.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var insertEmail = Cmd(connection, transaction, "INSERT INTO AuthorEmails(AuthorId,Email,CreatedAt) VALUES($id,$email,$now);");
                insertEmail.Parameters.AddWithValue("$id", id.Value); insertEmail.Parameters.AddWithValue("$email", email); insertEmail.Parameters.AddWithValue("$now", DatabaseService.Now()); insertEmail.ExecuteNonQuery();
            }
        }
        return map;
    }

    private Dictionary<long, long> ImportPapers(SqliteConnection connection, SqliteTransaction transaction, TransferPackage package, ZipArchive archive, List<string> createdFiles, List<string> supersededFiles)
    {
        var map = new Dictionary<long, long>();
        var used = new HashSet<long>();
        foreach (var paper in package.Papers)
        {
            var name = Required(paper.Name, "论文名称");
            var id = FindUnusedId(connection, transaction, "Papers", "Name", name, used);
            if (id is null)
            {
                using var insert = Cmd(connection, transaction, "INSERT INTO Papers(Name,Notes,CreatedAt,UpdatedAt) VALUES($name,$notes,$created,$now) RETURNING Id;");
                insert.Parameters.AddWithValue("$name", name); insert.Parameters.AddWithValue("$notes", paper.Notes ?? ""); insert.Parameters.AddWithValue("$created", ValidTime(paper.CreatedAt)); insert.Parameters.AddWithValue("$now", DatabaseService.Now());
                id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            else
            {
                using var update = Cmd(connection, transaction, "UPDATE Papers SET Name=$name,Notes=$notes,CreatedAt=$created,UpdatedAt=$now WHERE Id=$id;");
                update.Parameters.AddWithValue("$name", name); update.Parameters.AddWithValue("$notes", paper.Notes ?? ""); update.Parameters.AddWithValue("$created", ValidTime(paper.CreatedAt)); update.Parameters.AddWithValue("$now", DatabaseService.Now()); update.Parameters.AddWithValue("$id", id.Value); update.ExecuteNonQuery();
            }
            used.Add(id.Value); map[paper.SourceId] = id.Value;
            foreach (var attachment in paper.Attachments) ImportAttachment(connection, transaction, archive, id.Value, attachment, createdFiles, supersededFiles);
        }
        return map;
    }

    private void ImportAttachment(SqliteConnection connection, SqliteTransaction transaction, ZipArchive archive, long paperId, TransferAttachment attachment, List<string> createdFiles, List<string> supersededFiles)
    {
        var entry = archive.GetEntry(attachment.PackageEntry) ?? throw new InvalidDataException($"数据包缺少附件：{attachment.PackageEntry}");
        var paperDirectory = Path.Combine(database.Paths.AttachmentRoot, paperId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(paperDirectory);
        var finalPath = PaperService.GetAvailableAttachmentPath(paperDirectory, attachment.OriginalFileName);
        using (var input = entry.Open()) using (var output = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        createdFiles.Add(finalPath);
        var relative = Path.GetRelativePath(database.Paths.AttachmentRoot, finalPath).Replace(Path.DirectorySeparatorChar, '/');

        long? existingId = null; string? oldStoredPath = null;
        using (var find = Cmd(connection, transaction, "SELECT Id,StoredPath FROM PaperAttachments WHERE PaperId=$paper AND DisplayName=$name COLLATE NOCASE ORDER BY Id LIMIT 1;"))
        {
            find.Parameters.AddWithValue("$paper", paperId); find.Parameters.AddWithValue("$name", Required(attachment.DisplayName, "附件名称"));
            using var reader = find.ExecuteReader(); if (reader.Read()) { existingId = reader.GetInt64(0); oldStoredPath = reader.GetString(1); }
        }
        if (existingId is null)
        {
            using var insert = Cmd(connection, transaction, "INSERT INTO PaperAttachments(PaperId,DisplayName,OriginalFileName,StoredPath,CreatedAt) VALUES($paper,$display,$original,$stored,$created);");
            AddAttachmentParameters(insert, paperId, attachment, relative); insert.ExecuteNonQuery();
        }
        else
        {
            using var update = Cmd(connection, transaction, "UPDATE PaperAttachments SET DisplayName=$display,OriginalFileName=$original,StoredPath=$stored,CreatedAt=$created WHERE Id=$id;");
            update.Parameters.AddWithValue("$id", existingId.Value); AddAttachmentParameters(update, paperId, attachment, relative); update.ExecuteNonQuery();
            if (!string.IsNullOrWhiteSpace(oldStoredPath))
            {
                try { supersededFiles.Add(papers.ResolveAttachmentPath(oldStoredPath)); } catch { }
            }
        }
    }

    private static void AddAttachmentParameters(SqliteCommand command, long paperId, TransferAttachment attachment, string relative)
    {
        command.Parameters.AddWithValue("$paper", paperId); command.Parameters.AddWithValue("$display", Required(attachment.DisplayName, "附件名称")); command.Parameters.AddWithValue("$original", Path.GetFileName(Required(attachment.OriginalFileName, "附件原文件名"))); command.Parameters.AddWithValue("$stored", relative); command.Parameters.AddWithValue("$created", ValidTime(attachment.CreatedAt));
    }

    private static Dictionary<long, long> ImportWorkspaces(SqliteConnection connection, SqliteTransaction transaction, TransferPackage package)
    {
        var map = new Dictionary<long, long>();
        foreach (var workspace in package.Workspaces)
        {
            var name = Required(workspace.JournalName, "期刊栏目名称");
            long? id;
            using (var find = Cmd(connection, transaction, "SELECT Id FROM JournalWorkspaces WHERE JournalName=$name COLLATE NOCASE LIMIT 1;")) { find.Parameters.AddWithValue("$name", name); id = find.ExecuteScalar() is { } v ? Convert.ToInt64(v) : null; }
            long? journalId;
            using (var findJournal = Cmd(connection, transaction, "SELECT Id FROM Journals WHERE Name=$name COLLATE NOCASE ORDER BY Id LIMIT 1;")) { findJournal.Parameters.AddWithValue("$name", name); journalId = findJournal.ExecuteScalar() is { } v ? Convert.ToInt64(v) : null; }
            if (id is null)
            {
                using var insert = Cmd(connection, transaction, "INSERT INTO JournalWorkspaces(JournalId,JournalName,SubmissionLink,CreatedAt,UpdatedAt) VALUES($journal,$name,$link,$created,$updated) RETURNING Id;");
                AddWorkspaceParameters(insert, workspace, journalId); id = Convert.ToInt64(insert.ExecuteScalar());
            }
            else
            {
                using var update = Cmd(connection, transaction, "UPDATE JournalWorkspaces SET JournalId=$journal,SubmissionLink=$link,CreatedAt=$created,UpdatedAt=$updated WHERE Id=$id;");
                update.Parameters.AddWithValue("$id", id.Value); AddWorkspaceParameters(update, workspace, journalId); update.ExecuteNonQuery();
            }
            map[workspace.SourceId] = id.Value;
            using (var delete = Cmd(connection, transaction, "DELETE FROM JournalAccounts WHERE JournalWorkspaceId=$id;")) { delete.Parameters.AddWithValue("$id", id.Value); delete.ExecuteNonQuery(); }
            var accounts = workspace.Accounts.Where(account =>
                !(string.Equals(account.Label?.Trim(), "投稿账号", StringComparison.Ordinal) &&
                  string.IsNullOrWhiteSpace(account.AccountName) &&
                  string.IsNullOrEmpty(account.Password)));
            foreach (var account in accounts)
            {
                using var insertAccount = Cmd(connection, transaction, "INSERT INTO JournalAccounts(JournalWorkspaceId,Label,AccountName,PasswordCipher,CreatedAt,UpdatedAt) VALUES($workspace,$label,$account,$password,$created,$updated);");
                insertAccount.Parameters.AddWithValue("$workspace", id.Value); insertAccount.Parameters.AddWithValue("$label", string.IsNullOrWhiteSpace(account.Label) ? "投稿账号" : account.Label.Trim()); insertAccount.Parameters.AddWithValue("$account", account.AccountName?.Trim() ?? ""); insertAccount.Parameters.AddWithValue("$password", account.Password ?? ""); insertAccount.Parameters.AddWithValue("$created", ValidTime(account.CreatedAt)); insertAccount.Parameters.AddWithValue("$updated", ValidTime(account.UpdatedAt)); insertAccount.ExecuteNonQuery();
            }
        }
        return map;
    }

    private static void AddWorkspaceParameters(SqliteCommand command, TransferWorkspace workspace, long? journalId)
    {
        command.Parameters.AddWithValue("$journal", journalId is { } id ? id : DBNull.Value); command.Parameters.AddWithValue("$name", Required(workspace.JournalName, "期刊栏目名称")); command.Parameters.AddWithValue("$link", workspace.SubmissionLink?.Trim() ?? ""); command.Parameters.AddWithValue("$created", ValidTime(workspace.CreatedAt)); command.Parameters.AddWithValue("$updated", ValidTime(workspace.UpdatedAt));
    }

    private static void ImportSubmissions(SqliteConnection connection, SqliteTransaction transaction, TransferPackage package, Dictionary<long, long> paperMap, Dictionary<long, long> workspaceMap)
    {
        var workspaceByName = package.Workspaces.Where(x => workspaceMap.ContainsKey(x.SourceId)).GroupBy(x => x.JournalName, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => workspaceMap[x.First().SourceId], StringComparer.OrdinalIgnoreCase);
        foreach (var paper in package.Papers)
        foreach (var submission in paper.Submissions)
        {
            var paperId = paperMap[paper.SourceId];
            long? workspaceId = !string.IsNullOrWhiteSpace(submission.WorkspaceName) && workspaceByName.TryGetValue(submission.WorkspaceName, out var mapped) ? mapped : null;
            long? id;
            using (var find = Cmd(connection, transaction, "SELECT Id FROM PaperSubmissions WHERE PaperId=$paper AND JournalName=$journal COLLATE NOCASE AND RecordedAt=$time ORDER BY Id LIMIT 1;"))
            {
                find.Parameters.AddWithValue("$paper", paperId); find.Parameters.AddWithValue("$journal", Required(submission.JournalName, "投稿期刊名称")); find.Parameters.AddWithValue("$time", ValidTime(submission.RecordedAt)); id = find.ExecuteScalar() is { } v ? Convert.ToInt64(v) : null;
            }
            var status = PaperService.SubmissionStatuses.Contains(submission.CurrentStatus) ? submission.CurrentStatus : "未投稿";
            if (id is null)
            {
                using var insert = Cmd(connection, transaction, "INSERT INTO PaperSubmissions(PaperId,JournalWorkspaceId,JournalName,CurrentStatus,RecordedAt) VALUES($paper,$workspace,$journal,$status,$time) RETURNING Id;");
                AddSubmissionParameters(insert, paperId, workspaceId, submission, status); id = Convert.ToInt64(insert.ExecuteScalar());
            }
            else
            {
                using var update = Cmd(connection, transaction, "UPDATE PaperSubmissions SET JournalWorkspaceId=$workspace,JournalName=$journal,CurrentStatus=$status,RecordedAt=$time WHERE Id=$id;");
                update.Parameters.AddWithValue("$id", id.Value); AddSubmissionParameters(update, paperId, workspaceId, submission, status); update.ExecuteNonQuery();
                using var deleteNotes = Cmd(connection, transaction, "DELETE FROM SubmissionNotes WHERE PaperSubmissionId=$id;"); deleteNotes.Parameters.AddWithValue("$id", id.Value); deleteNotes.ExecuteNonQuery();
            }
            var groupMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var note in submission.Notes)
            {
                var sourceGroup = string.IsNullOrWhiteSpace(note.VersionGroupId) ? Guid.NewGuid().ToString("N") : note.VersionGroupId;
                if (!groupMap.TryGetValue(sourceGroup, out var targetGroup)) groupMap[sourceGroup] = targetGroup = Guid.NewGuid().ToString("N");
                using var insertNote = Cmd(connection, transaction, "INSERT INTO SubmissionNotes(PaperSubmissionId,VersionGroupId,VersionNumber,Content,RecordedAt,IsCurrent) VALUES($submission,$group,$version,$content,$time,$current);");
                insertNote.Parameters.AddWithValue("$submission", id.Value); insertNote.Parameters.AddWithValue("$group", targetGroup); insertNote.Parameters.AddWithValue("$version", Math.Max(1, note.VersionNumber)); insertNote.Parameters.AddWithValue("$content", Required(note.Content, "状态纪要")); insertNote.Parameters.AddWithValue("$time", ValidTime(note.RecordedAt)); insertNote.Parameters.AddWithValue("$current", note.IsCurrent ? 1 : 0); insertNote.ExecuteNonQuery();
            }
        }
    }

    private static void AddSubmissionParameters(SqliteCommand command, long paperId, long? workspaceId, TransferSubmission submission, string status)
    {
        command.Parameters.AddWithValue("$paper", paperId); command.Parameters.AddWithValue("$workspace", workspaceId is { } id ? id : DBNull.Value); command.Parameters.AddWithValue("$journal", Required(submission.JournalName, "投稿期刊名称")); command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$time", ValidTime(submission.RecordedAt));
    }

    private static void ImportWorkspaceLinks(SqliteConnection connection, SqliteTransaction transaction, TransferPackage package, Dictionary<long, long> paperMap, Dictionary<long, long> workspaceMap)
    {
        foreach (var workspace in package.Workspaces)
        foreach (var sourcePaperId in workspace.LinkedPaperSourceIds)
        {
            if (!workspaceMap.TryGetValue(workspace.SourceId, out var workspaceId) || !paperMap.TryGetValue(sourcePaperId, out var paperId)) continue;
            using var link = Cmd(connection, transaction, "INSERT OR IGNORE INTO JournalWorkspacePapers(JournalWorkspaceId,PaperId,CreatedAt) VALUES($workspace,$paper,$now);");
            link.Parameters.AddWithValue("$workspace", workspaceId); link.Parameters.AddWithValue("$paper", paperId); link.Parameters.AddWithValue("$now", DatabaseService.Now()); link.ExecuteNonQuery();
        }
    }

    private void MoveSupersededFilesToTrash(IEnumerable<string> paths)
    {
        var unique = paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
        if (unique.Count == 0) return;
        var trash = Path.Combine(database.Paths.AttachmentRoot, ".trash", "import-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(trash);
        foreach (var path in unique)
            try { File.Move(path, Path.Combine(trash, Guid.NewGuid().ToString("N") + Path.GetExtension(path))); } catch { }
    }

    private static long? FindUnusedId(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string value, HashSet<long> used)
    {
        using var command = Cmd(connection, transaction, $"SELECT Id FROM {table} WHERE {column}=$value COLLATE NOCASE ORDER BY Id;"); command.Parameters.AddWithValue("$value", value); using var reader = command.ExecuteReader(); while (reader.Read()) { var id = reader.GetInt64(0); if (!used.Contains(id)) return id; } return null;
    }
    private static SqliteCommand Cmd(SqliteConnection connection, SqliteTransaction transaction, string sql) { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"{name}不能为空。") : value.Trim();
    private static string ValidTime(string? value) => DateTimeOffset.TryParse(value, out _) ? value! : DatabaseService.Now();
    private static string SafeExtension(string fileName) { var extension = Path.GetExtension(Path.GetFileName(fileName)); return extension.Length <= 32 && extension.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? extension : ""; }

    private static void ValidatePackage(TransferPackage package, ZipArchive archive)
    {
        if (package.Version != PackageVersion) throw new InvalidDataException($"不支持的数据包版本：{package.Version}。");
        if (package.Papers.Count > 100000 || package.Authors.Count > 100000 || package.Workspaces.Count > 100000) throw new InvalidDataException("数据包记录数量异常。");
        long total = 0;
        foreach (var attachment in package.Papers.SelectMany(x => x.Attachments))
        {
            if (!attachment.PackageEntry.StartsWith("attachments/", StringComparison.Ordinal) || attachment.PackageEntry.Contains("..", StringComparison.Ordinal)) throw new InvalidDataException("附件条目路径无效。");
            var entry = archive.GetEntry(attachment.PackageEntry) ?? throw new InvalidDataException($"数据包缺少附件：{attachment.PackageEntry}");
            checked { total += entry.Length; }
            if (total > 20L * 1024 * 1024 * 1024) throw new InvalidDataException("数据包附件总量超过 20 GB 限制。");
        }
    }

    private sealed class TransferPackage { public int Version { get; set; } public string ExportedAt { get; set; } = ""; public List<TransferPaper> Papers { get; set; } = []; public List<TransferAuthor> Authors { get; set; } = []; public List<TransferWorkspace> Workspaces { get; set; } = []; }
    private sealed class TransferPaper { public long SourceId { get; set; } public string Name { get; set; } = ""; public string Notes { get; set; } = ""; public string CreatedAt { get; set; } = ""; public List<TransferAttachment> Attachments { get; set; } = []; public List<TransferSubmission> Submissions { get; set; } = []; }
    private sealed class TransferAttachment { public long SourceId { get; set; } public string DisplayName { get; set; } = ""; public string OriginalFileName { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string PackageEntry { get; set; } = ""; }
    private sealed class TransferSubmission { public long SourceId { get; set; } public string JournalName { get; set; } = ""; public string WorkspaceName { get; set; } = ""; public string CurrentStatus { get; set; } = "未投稿"; public string RecordedAt { get; set; } = ""; public List<TransferNote> Notes { get; set; } = []; }
    private sealed class TransferNote { public string VersionGroupId { get; set; } = ""; public int VersionNumber { get; set; } public string Content { get; set; } = ""; public string RecordedAt { get; set; } = ""; public bool IsCurrent { get; set; } }
    private sealed class TransferAuthor { public long SourceId { get; set; } public string Name { get; set; } = ""; public string CreatedAt { get; set; } = ""; public List<string> Emails { get; set; } = []; }
    private sealed class TransferWorkspace { public long SourceId { get; set; } public string JournalName { get; set; } = ""; public string SubmissionLink { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; public List<TransferAccount> Accounts { get; set; } = []; public List<long> LinkedPaperSourceIds { get; set; } = []; }
    private sealed class TransferAccount { public string Label { get; set; } = "投稿账号"; public string AccountName { get; set; } = ""; public string Password { get; set; } = ""; public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = ""; }
}

public sealed record TransferResult(int PaperCount, int WorkspaceCount, int AuthorCount, int AttachmentCount);
