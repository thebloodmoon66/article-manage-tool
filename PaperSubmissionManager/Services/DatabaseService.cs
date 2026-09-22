using Microsoft.Data.Sqlite;

namespace PaperSubmissionManager.Services;

public sealed class DatabaseService
{
    public DatabaseService(AppPaths paths)
    {
        Paths = paths;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public AppPaths Paths { get; }
    public string ConnectionString { get; }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA temp_store = MEMORY;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS Journals (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                NormalizedKey TEXT NOT NULL UNIQUE,
                Issn TEXT NOT NULL DEFAULT '',
                Name TEXT NOT NULL,
                ImpactFactorText TEXT NOT NULL DEFAULT '',
                ImpactFactor REAL NULL,
                Quartile TEXT NOT NULL DEFAULT '',
                SubjectArea TEXT NOT NULL DEFAULT '',
                OaText TEXT NOT NULL DEFAULT '',
                IsOa INTEGER NULL,
                AnnualArticlesText TEXT NOT NULL DEFAULT '',
                AnnualArticles INTEGER NULL,
                SourceFile TEXT NOT NULL DEFAULT '',
                SourceSheet TEXT NOT NULL DEFAULT '',
                ImportedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                CHECK(length(trim(Name)) > 0)
            );
            CREATE INDEX IF NOT EXISTS IX_Journals_Name ON Journals(Name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_Journals_Issn ON Journals(Issn COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_Journals_Filter ON Journals(Quartile, IsOa, ImpactFactor, AnnualArticles);

            CREATE TABLE IF NOT EXISTS JournalClassifications (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JournalId INTEGER NOT NULL REFERENCES Journals(Id) ON DELETE CASCADE,
                Category TEXT NOT NULL DEFAULT '未分类',
                Rank TEXT NOT NULL DEFAULT '',
                SequenceNumber INTEGER NOT NULL DEFAULT 0,
                SourceSheet TEXT NOT NULL DEFAULT '',
                UpdatedAt TEXT NOT NULL,
                UNIQUE(JournalId, Category)
            );
            CREATE INDEX IF NOT EXISTS IX_JournalClassifications_Filter ON JournalClassifications(Category, Rank);

            CREATE TABLE IF NOT EXISTS Papers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Notes TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PaperAttachments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PaperId INTEGER NOT NULL REFERENCES Papers(Id) ON DELETE CASCADE,
                DisplayName TEXT NOT NULL,
                OriginalFileName TEXT NOT NULL,
                StoredPath TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PaperAttachments_PaperId ON PaperAttachments(PaperId);

            CREATE TABLE IF NOT EXISTS JournalWorkspaces (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JournalId INTEGER NULL REFERENCES Journals(Id) ON DELETE SET NULL,
                JournalName TEXT NOT NULL COLLATE NOCASE UNIQUE,
                SubmissionLink TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PaperSubmissions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PaperId INTEGER NOT NULL REFERENCES Papers(Id) ON DELETE CASCADE,
                JournalWorkspaceId INTEGER NULL REFERENCES JournalWorkspaces(Id) ON DELETE SET NULL,
                JournalName TEXT NOT NULL,
                CurrentStatus TEXT NOT NULL DEFAULT '未投稿',
                RecordedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PaperSubmissions_PaperId ON PaperSubmissions(PaperId);

            CREATE TABLE IF NOT EXISTS SubmissionNotes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PaperSubmissionId INTEGER NOT NULL REFERENCES PaperSubmissions(Id) ON DELETE CASCADE,
                VersionGroupId TEXT NOT NULL,
                VersionNumber INTEGER NOT NULL,
                Content TEXT NOT NULL,
                RecordedAt TEXT NOT NULL,
                IsCurrent INTEGER NOT NULL DEFAULT 1,
                UNIQUE(VersionGroupId, VersionNumber)
            );
            CREATE INDEX IF NOT EXISTS IX_SubmissionNotes_Current ON SubmissionNotes(PaperSubmissionId, IsCurrent);

            CREATE TABLE IF NOT EXISTS Authors (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS AuthorEmails (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AuthorId INTEGER NOT NULL REFERENCES Authors(Id) ON DELETE CASCADE,
                Email TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AuthorEmails_AuthorId ON AuthorEmails(AuthorId);

            CREATE TABLE IF NOT EXISTS JournalAccounts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JournalWorkspaceId INTEGER NOT NULL REFERENCES JournalWorkspaces(Id) ON DELETE CASCADE,
                Label TEXT NOT NULL DEFAULT '投稿账号',
                AccountName TEXT NOT NULL DEFAULT '',
                PasswordCipher TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_JournalAccounts_WorkspaceId ON JournalAccounts(JournalWorkspaceId);

            CREATE TABLE IF NOT EXISTS JournalWorkspacePapers (
                JournalWorkspaceId INTEGER NOT NULL REFERENCES JournalWorkspaces(Id) ON DELETE CASCADE,
                PaperId INTEGER NOT NULL REFERENCES Papers(Id) ON DELETE CASCADE,
                CreatedAt TEXT NOT NULL,
                PRIMARY KEY(JournalWorkspaceId, PaperId)
            );
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "PaperSubmissions", "CurrentStatus", "TEXT NOT NULL DEFAULT '未投稿'");
        RepairPaperWorkspaceLinks(connection);
    }

    private static void RepairPaperWorkspaceLinks(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO JournalWorkspacePapers(JournalWorkspaceId,PaperId,CreatedAt)
            SELECT s.JournalWorkspaceId,s.PaperId,s.RecordedAt
            FROM PaperSubmissions s
            WHERE s.JournalWorkspaceId IS NOT NULL;

            INSERT INTO PaperSubmissions(
                PaperId,JournalWorkspaceId,JournalName,CurrentStatus,RecordedAt)
            SELECT link.PaperId,w.Id,w.JournalName,'未投稿',link.CreatedAt
            FROM JournalWorkspacePapers link
            JOIN JournalWorkspaces w ON w.Id=link.JournalWorkspaceId
            WHERE NOT EXISTS(
                SELECT 1 FROM PaperSubmissions s
                WHERE s.PaperId=link.PaperId
                  AND s.JournalWorkspaceId=link.JournalWorkspaceId);
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return;
        }
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }

    public static string Now() => DateTimeOffset.Now.ToString("O");
}
