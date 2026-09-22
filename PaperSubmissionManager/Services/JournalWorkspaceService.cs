using Microsoft.Data.Sqlite;
using PaperSubmissionManager.Models;

namespace PaperSubmissionManager.Services;

/// <summary>
/// Manages journal submission workspaces.  A workspace's journal name is fixed at
/// creation time; journal metadata is deliberately queried live from Journals and
/// JournalClassifications instead of being copied into the workspace.
/// </summary>
public sealed class JournalWorkspaceService(DatabaseService database)
{
    private const string DefaultAccountLabel = "投稿账号";

    public List<JournalWorkspaceRecord> List()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.Id, w.JournalId, w.JournalName, w.SubmissionLink,
                   w.CreatedAt, w.UpdatedAt,
                   (SELECT COUNT(*) FROM JournalWorkspacePapers p WHERE p.JournalWorkspaceId=w.Id),
                   (SELECT COUNT(*) FROM JournalAccounts a WHERE a.JournalWorkspaceId=w.Id)
            FROM JournalWorkspaces w
            ORDER BY w.JournalName COLLATE NOCASE, w.Id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<JournalWorkspaceRecord>();
        while (reader.Read()) rows.Add(ReadWorkspace(reader));
        return rows;
    }

    public JournalWorkspaceRecord? Get(long workspaceId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.Id, w.JournalId, w.JournalName, w.SubmissionLink,
                   w.CreatedAt, w.UpdatedAt,
                   (SELECT COUNT(*) FROM JournalWorkspacePapers p WHERE p.JournalWorkspaceId=w.Id),
                   (SELECT COUNT(*) FROM JournalAccounts a WHERE a.JournalWorkspaceId=w.Id)
            FROM JournalWorkspaces w
            WHERE w.Id=$id;
            """;
        command.Parameters.AddWithValue("$id", workspaceId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadWorkspace(reader) : null;
    }

    public long CreateFromJournal(long journalId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        string journalName;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT Name FROM Journals WHERE Id=$id;";
            lookup.Parameters.AddWithValue("$id", journalId);
            journalName = lookup.ExecuteScalar() as string
                ?? throw new InvalidOperationException("所选期刊不存在或已被删除。");
        }

        var workspaceId = InsertWorkspace(connection, transaction, journalId, journalName);
        transaction.Commit();
        return workspaceId;
    }

    public long CreateCustom(string journalName)
    {
        journalName = RequireName(journalName);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var workspaceId = InsertWorkspace(connection, transaction, null, journalName);
        transaction.Commit();
        return workspaceId;
    }

    public int RemoveLegacyEmptyAccounts()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM JournalAccounts
            WHERE Label=$label
              AND trim(AccountName)=''
              AND PasswordCipher='';
            """;
        command.Parameters.AddWithValue("$label", DefaultAccountLabel);
        return command.ExecuteNonQuery();
    }

    public void Delete(long workspaceId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // FK rules preserve papers, submission history and notes, while clearing the
        // obsolete workspace jump target and cascading workspace-owned details.
        command.CommandText = "DELETE FROM JournalWorkspaces WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", workspaceId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("期刊投稿栏目不存在或已被删除。");
        transaction.Commit();
    }

    public void SaveSubmissionLink(long workspaceId, string? submissionLink)
    {
        var normalized = ValidateSubmissionLink(submissionLink);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE JournalWorkspaces
            SET SubmissionLink=$link, UpdatedAt=$now
            WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$link", normalized);
        command.Parameters.AddWithValue("$now", DatabaseService.Now());
        command.Parameters.AddWithValue("$id", workspaceId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("期刊投稿工作台不存在或已被删除。");
    }

    public List<JournalAccountRecord> ListAccounts(long workspaceId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, JournalWorkspaceId, Label, AccountName, PasswordCipher
            FROM JournalAccounts
            WHERE JournalWorkspaceId=$workspace
            ORDER BY CreatedAt, Id;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId);
        using var reader = command.ExecuteReader();
        var rows = new List<JournalAccountRecord>();
        while (reader.Read())
        {
            rows.Add(new JournalAccountRecord
            {
                Id = reader.GetInt64(0),
                JournalWorkspaceId = reader.GetInt64(1),
                Label = reader.GetString(2),
                AccountName = reader.GetString(3),
                Password = reader.GetString(4)
            });
        }
        return rows;
    }

    public long AddAccount(long workspaceId, string? label = null, string? accountName = null, string? password = null)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsureWorkspaceExists(connection, transaction, workspaceId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO JournalAccounts(
                JournalWorkspaceId, Label, AccountName, PasswordCipher, CreatedAt, UpdatedAt)
            VALUES($workspace,$label,$account,$password,$now,$now)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId);
        command.Parameters.AddWithValue("$label", NormalizeLabel(label));
        command.Parameters.AddWithValue("$account", accountName?.Trim() ?? "");
        command.Parameters.AddWithValue("$password", password ?? "");
        command.Parameters.AddWithValue("$now", DatabaseService.Now());
        var accountId = Convert.ToInt64(command.ExecuteScalar());
        TouchWorkspace(connection, transaction, workspaceId);
        transaction.Commit();
        return accountId;
    }

    public void UpdateAccount(long accountId, string? label, string? accountName, string? password)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        long workspaceId;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE JournalAccounts
                SET Label=$label, AccountName=$account, PasswordCipher=$password, UpdatedAt=$now
                WHERE Id=$id
                RETURNING JournalWorkspaceId;
                """;
            command.Parameters.AddWithValue("$label", NormalizeLabel(label));
            command.Parameters.AddWithValue("$account", accountName?.Trim() ?? "");
            command.Parameters.AddWithValue("$password", password ?? "");
            command.Parameters.AddWithValue("$now", DatabaseService.Now());
            command.Parameters.AddWithValue("$id", accountId);
            workspaceId = command.ExecuteScalar() is { } value
                ? Convert.ToInt64(value)
                : throw new InvalidOperationException("投稿账号记录不存在或已被删除。");
        }
        TouchWorkspace(connection, transaction, workspaceId);
        transaction.Commit();
    }

    public void DeleteAccount(long accountId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        long workspaceId;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT JournalWorkspaceId FROM JournalAccounts WHERE Id=$id;";
            lookup.Parameters.AddWithValue("$id", accountId);
            workspaceId = lookup.ExecuteScalar() is { } value
                ? Convert.ToInt64(value)
                : throw new InvalidOperationException("投稿账号记录不存在或已被删除。");
        }
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM JournalAccounts WHERE Id=$id;";
            delete.Parameters.AddWithValue("$id", accountId);
            if (delete.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("投稿账号记录不存在或已被删除。");
        }
        TouchWorkspace(connection, transaction, workspaceId);
        transaction.Commit();
    }

    public void LinkPaper(long workspaceId, long paperId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsureWorkspaceExists(connection, transaction, workspaceId);
        EnsurePaperExists(connection, transaction, paperId);
        var now = DatabaseService.Now();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO JournalWorkspacePapers(JournalWorkspaceId,PaperId,CreatedAt)
                VALUES($workspace,$paper,$now);
                """;
            command.Parameters.AddWithValue("$workspace", workspaceId);
            command.Parameters.AddWithValue("$paper", paperId);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }
        using (var submission = connection.CreateCommand())
        {
            submission.Transaction = transaction;
            submission.CommandText = """
                INSERT INTO PaperSubmissions(
                    PaperId, JournalWorkspaceId, JournalName, CurrentStatus, RecordedAt)
                SELECT $paper, w.Id, w.JournalName, '未投稿', $now
                FROM JournalWorkspaces w
                WHERE w.Id=$workspace
                  AND NOT EXISTS(
                      SELECT 1 FROM PaperSubmissions s
                      WHERE s.PaperId=$paper AND s.JournalWorkspaceId=$workspace);
                """;
            submission.Parameters.AddWithValue("$workspace", workspaceId);
            submission.Parameters.AddWithValue("$paper", paperId);
            submission.Parameters.AddWithValue("$now", now);
            submission.ExecuteNonQuery();
        }
        TouchWorkspace(connection, transaction, workspaceId);
        transaction.Commit();
    }

    public void UnlinkPaper(long workspaceId, long paperId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsureWorkspaceExists(connection, transaction, workspaceId);
        using (var submissions = connection.CreateCommand())
        {
            submissions.Transaction = transaction;
            submissions.CommandText = """
                DELETE FROM PaperSubmissions
                WHERE JournalWorkspaceId=$workspace AND PaperId=$paper;
                """;
            submissions.Parameters.AddWithValue("$workspace", workspaceId);
            submissions.Parameters.AddWithValue("$paper", paperId);
            submissions.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM JournalWorkspacePapers
                WHERE JournalWorkspaceId=$workspace AND PaperId=$paper;
                """;
            command.Parameters.AddWithValue("$workspace", workspaceId);
            command.Parameters.AddWithValue("$paper", paperId);
            command.ExecuteNonQuery();
        }
        TouchWorkspace(connection, transaction, workspaceId);
        transaction.Commit();
    }

    public List<PaperRecord> ListLinkedPapers(long workspaceId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id, p.Name, p.Notes, p.CreatedAt,
                   (SELECT COUNT(*) FROM PaperAttachments a WHERE a.PaperId=p.Id),
                   (SELECT COUNT(*) FROM PaperSubmissions s WHERE s.PaperId=p.Id)
            FROM JournalWorkspacePapers wp
            JOIN Papers p ON p.Id=wp.PaperId
            WHERE wp.JournalWorkspaceId=$workspace
            ORDER BY wp.CreatedAt DESC, p.Name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId);
        using var reader = command.ExecuteReader();
        var rows = new List<PaperRecord>();
        while (reader.Read())
        {
            rows.Add(new PaperRecord
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Notes = reader.GetString(2),
                CreatedAt = reader.GetString(3),
                AttachmentCount = reader.GetInt32(4),
                SubmissionCount = reader.GetInt32(5)
            });
        }
        return rows;
    }

    /// <summary>
    /// Returns every current classification row for the journal backing a workspace.
    /// Custom workspaces, and imported journals later removed from the database, return an empty list.
    /// </summary>
    public List<JournalRecord> GetJournalRecords(long workspaceId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.Id, j.Id, c.SequenceNumber, c.Category, c.Rank, j.Issn, j.Name,
                   j.ImpactFactorText, j.ImpactFactor, j.Quartile, j.SubjectArea,
                   j.OaText, j.IsOa, j.AnnualArticlesText, j.AnnualArticles,
                   j.SourceFile, c.SourceSheet, j.UpdatedAt
            FROM JournalWorkspaces w
            JOIN Journals j ON j.Id=w.JournalId
            JOIN JournalClassifications c ON c.JournalId=j.Id
            WHERE w.Id=$workspace
            ORDER BY c.Category COLLATE NOCASE,
                     CASE c.Rank WHEN 'A类' THEN 0 WHEN 'B类' THEN 1 ELSE 2 END,
                     c.SequenceNumber, c.Id;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId);
        using var reader = command.ExecuteReader();
        var rows = new List<JournalRecord>();
        while (reader.Read()) rows.Add(ReadJournal(reader));
        return rows;
    }

    private static long InsertWorkspace(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? journalId,
        string journalName)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO JournalWorkspaces(JournalId,JournalName,SubmissionLink,CreatedAt,UpdatedAt)
                VALUES($journal,$name,'',$now,$now)
                RETURNING Id;
                """;
            command.Parameters.AddWithValue("$journal", journalId is { } id ? id : DBNull.Value);
            command.Parameters.AddWithValue("$name", RequireName(journalName));
            command.Parameters.AddWithValue("$now", DatabaseService.Now());
            return Convert.ToInt64(command.ExecuteScalar());
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("同名期刊投稿工作台已经存在。", exception);
        }
    }

    private static void EnsureWorkspaceExists(SqliteConnection connection, SqliteTransaction transaction, long workspaceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM JournalWorkspaces WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", workspaceId);
        if (command.ExecuteScalar() is null)
            throw new InvalidOperationException("期刊投稿工作台不存在或已被删除。");
    }

    private static void EnsurePaperExists(SqliteConnection connection, SqliteTransaction transaction, long paperId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM Papers WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", paperId);
        if (command.ExecuteScalar() is null)
            throw new InvalidOperationException("论文记录不存在或已被删除。");
    }

    private static void TouchWorkspace(SqliteConnection connection, SqliteTransaction transaction, long workspaceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE JournalWorkspaces SET UpdatedAt=$now WHERE Id=$id;";
        command.Parameters.AddWithValue("$now", DatabaseService.Now());
        command.Parameters.AddWithValue("$id", workspaceId);
        command.ExecuteNonQuery();
    }

    private static string ValidateSubmissionLink(string? value)
    {
        var link = value?.Trim() ?? "";
        if (link.Length == 0) return "";
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("投稿链接只能为空，或使用 http/https 完整网址。");
        return uri.AbsoluteUri;
    }

    private static string RequireName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidOperationException("期刊名称不能为空。");
        return name;
    }

    private static string NormalizeLabel(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DefaultAccountLabel : value.Trim();

    private static JournalWorkspaceRecord ReadWorkspace(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        JournalId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
        JournalName = reader.GetString(2),
        SubmissionLink = reader.GetString(3),
        CreatedAt = reader.GetString(4),
        UpdatedAt = reader.GetString(5),
        PaperCount = reader.GetInt32(6),
        AccountCount = reader.GetInt32(7)
    };

    private static JournalRecord ReadJournal(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        JournalId = reader.GetInt64(1),
        SequenceNumber = reader.GetInt32(2),
        Category = reader.GetString(3),
        Rank = reader.GetString(4),
        Issn = reader.GetString(5),
        Name = reader.GetString(6),
        ImpactFactorText = reader.GetString(7),
        ImpactFactor = reader.IsDBNull(8) ? null : reader.GetDouble(8),
        Quartile = reader.GetString(9),
        SubjectArea = reader.GetString(10),
        OaText = reader.GetString(11),
        IsOa = reader.IsDBNull(12) ? null : reader.GetInt32(12) == 1,
        AnnualArticlesText = reader.GetString(13),
        AnnualArticles = reader.IsDBNull(14) ? null : reader.GetInt32(14),
        SourceFile = reader.GetString(15),
        SourceSheet = reader.GetString(16),
        UpdatedAt = reader.GetString(17)
    };
}
