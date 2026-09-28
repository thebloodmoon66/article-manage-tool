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

    public long AddRound(long submissionId, string opinion, string reply)
    {
        opinion = RequireOpinion(opinion);
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
        command.Parameters.AddWithValue("$opinion", opinion);
        command.Parameters.AddWithValue("$reply", reply.Trim());
        command.Parameters.AddWithValue("$time", DatabaseService.Now());
        var id = command.ExecuteScalar() as long? ?? throw new InvalidOperationException("请先选择有效的投稿期刊。");
        transaction.Commit();
        return id;
    }

    public long AddVersion(long selectedVersionId, string opinion, string reply)
    {
        opinion = RequireOpinion(opinion);
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
        command.Parameters.AddWithValue("$opinion", opinion);
        command.Parameters.AddWithValue("$reply", reply.Trim());
        command.Parameters.AddWithValue("$time", DatabaseService.Now());
        var id = command.ExecuteScalar() as long? ?? throw new InvalidOperationException("请选择该轮的最新版本后再保存。");
        transaction.Commit();
        return id;
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

    private static string RequireOpinion(string? opinion) =>
        string.IsNullOrWhiteSpace(opinion) ? throw new ArgumentException("请输入返修意见。", nameof(opinion)) : opinion.Trim();
}
