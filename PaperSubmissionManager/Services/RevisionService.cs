using Microsoft.Data.Sqlite;
using PaperSubmissionManager.Models;

namespace PaperSubmissionManager.Services;

public sealed class RevisionService(DatabaseService database)
{
    public List<RevisionRecord> List(long submissionId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,PaperSubmissionId,RoundNumber,VersionNumber,Opinion,Reply,RecordedAt,
                   VersionNumber = (SELECT MAX(other.VersionNumber) FROM RevisionOpinions other
                                    WHERE other.PaperSubmissionId = RevisionOpinions.PaperSubmissionId
                                      AND other.RoundNumber = RevisionOpinions.RoundNumber)
            FROM RevisionOpinions WHERE PaperSubmissionId=$submission
            ORDER BY RoundNumber DESC,VersionNumber DESC;
            """;
        command.Parameters.AddWithValue("$submission", submissionId);
        using var reader = command.ExecuteReader();
        var result = new List<RevisionRecord>();
        while (reader.Read()) result.Add(new RevisionRecord
        {
            Id = reader.GetInt64(0), PaperSubmissionId = reader.GetInt64(1),
            RoundNumber = reader.GetInt32(2), VersionNumber = reader.GetInt32(3),
            Opinion = reader.GetString(4), Reply = reader.GetString(5),
            RecordedAt = reader.GetString(6), IsLatest = reader.GetInt32(7) == 1
        });
        return result;
    }

    public List<RevisionOpinionItemRecord> ListItems(long revisionId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Number,Opinion,Reply,Notes FROM RevisionOpinionItems WHERE RevisionId=$revision ORDER BY Number;";
        command.Parameters.AddWithValue("$revision", revisionId);
        using var reader = command.ExecuteReader();
        var items = new List<RevisionOpinionItemRecord>();
        while (reader.Read()) items.Add(new RevisionOpinionItemRecord
        {
            Id = reader.GetInt64(0), Number = reader.GetInt32(1), Opinion = reader.GetString(2), Reply = reader.GetString(3), Notes = reader.GetString(4)
        });
        return items;
    }

    public long AddRound(long submissionId, IReadOnlyList<RevisionOpinionItemRecord> items)
    {
        var validated = ValidateItems(items);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO RevisionOpinions(PaperSubmissionId,RoundNumber,VersionNumber,Opinion,Reply,RecordedAt)
            SELECT Id,
                   (SELECT COALESCE(MAX(RoundNumber),0)+1 FROM RevisionOpinions WHERE PaperSubmissionId=$submission),
                   1,$opinion,$reply,$time
            FROM PaperSubmissions WHERE Id=$submission RETURNING Id;
            """;
        command.Parameters.AddWithValue("$submission", submissionId);
        command.Parameters.AddWithValue("$opinion", validated[0].Opinion);
        command.Parameters.AddWithValue("$reply", validated[0].Reply);
        command.Parameters.AddWithValue("$time", DatabaseService.Now());
        var id = command.ExecuteScalar() as long? ?? throw new InvalidOperationException("请先选择有效的投稿期刊。");
        InsertItems(connection, transaction, id, validated);
        transaction.Commit();
        return id;
    }

    public long AddVersion(long selectedVersionId, IReadOnlyList<RevisionOpinionItemRecord> items)
    {
        var validated = ValidateItems(items);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO RevisionOpinions(PaperSubmissionId,RoundNumber,VersionNumber,Opinion,Reply,RecordedAt)
            SELECT selected.PaperSubmissionId,selected.RoundNumber,selected.VersionNumber+1,
                   $opinion,$reply,$time
            FROM RevisionOpinions selected
            WHERE selected.Id=$id AND selected.VersionNumber=(
                SELECT MAX(other.VersionNumber) FROM RevisionOpinions other
                WHERE other.PaperSubmissionId=selected.PaperSubmissionId
                  AND other.RoundNumber=selected.RoundNumber)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$id", selectedVersionId);
        command.Parameters.AddWithValue("$opinion", validated[0].Opinion);
        command.Parameters.AddWithValue("$reply", validated[0].Reply);
        command.Parameters.AddWithValue("$time", DatabaseService.Now());
        var id = command.ExecuteScalar() as long? ?? throw new InvalidOperationException("请选择该轮的最新版本后再保存。");
        InsertItems(connection, transaction, id, validated);
        transaction.Commit();
        return id;
    }

    public int AppendComments(long revisionId, IReadOnlyList<string> comments)
    {
        if (comments.Count == 0) throw new ArgumentException("没有可导入的返修意见。", nameof(comments));
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = """
            SELECT COALESCE(MAX(i.Number),0)
            FROM RevisionOpinions r LEFT JOIN RevisionOpinionItems i ON i.RevisionId=r.Id
            WHERE r.Id=$revision GROUP BY r.Id;
            """;
        count.Parameters.AddWithValue("$revision", revisionId);
        var nextNumber = count.ExecuteScalar() is { } value
            ? Convert.ToInt32(value) + 1
            : throw new InvalidOperationException("选中的返修版本不存在或已被删除。");
        foreach (var comment in comments)
        {
            if (string.IsNullOrWhiteSpace(comment)) throw new ArgumentException("返修意见不能为空。", nameof(comments));
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO RevisionOpinionItems(RevisionId,Number,Opinion,Reply,Notes) VALUES($revision,$number,$opinion,'','');";
            insert.Parameters.AddWithValue("$revision", revisionId);
            insert.Parameters.AddWithValue("$number", nextNumber++);
            insert.Parameters.AddWithValue("$opinion", comment);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return comments.Count;
    }

    public void DeleteRound(long submissionId, int roundNumber)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM RevisionOpinions WHERE PaperSubmissionId=$submission AND RoundNumber=$round;";
        command.Parameters.AddWithValue("$submission", submissionId);
        command.Parameters.AddWithValue("$round", roundNumber);
        if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("返修意见已不存在。");
    }

    private static List<(string Opinion, string Reply, string Notes)> ValidateItems(IReadOnlyList<RevisionOpinionItemRecord> items)
    {
        if (items.Count == 0) throw new ArgumentException("请至少添加一条返修意见。", nameof(items));
        var result = new List<(string Opinion, string Reply, string Notes)>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Opinion)) throw new ArgumentException("每条返修意见都不能为空。", nameof(items));
            result.Add((item.Opinion.Trim(), item.Reply?.Trim() ?? "", item.Notes?.Trim() ?? ""));
        }
        return result;
    }

    private static void InsertItems(SqliteConnection connection, SqliteTransaction transaction, long revisionId, List<(string Opinion, string Reply, string Notes)> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO RevisionOpinionItems(RevisionId,Number,Opinion,Reply,Notes) VALUES($revision,$number,$opinion,$reply,$notes);";
            insert.Parameters.AddWithValue("$revision", revisionId);
            insert.Parameters.AddWithValue("$number", i + 1);
            insert.Parameters.AddWithValue("$opinion", items[i].Opinion);
            insert.Parameters.AddWithValue("$reply", items[i].Reply);
            insert.Parameters.AddWithValue("$notes", items[i].Notes);
            insert.ExecuteNonQuery();
        }
    }
}
