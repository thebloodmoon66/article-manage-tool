using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using PaperSubmissionManager.Models;

namespace PaperSubmissionManager.Services;

public sealed partial class JournalImportService(DatabaseService database, BackupService backups)
{
    private static readonly string[] HeaderTokens = ["序号", "等级", "ISSN号", "期刊名称", "IF值", "分区", "学科领域", "是否OA", "年文章数"];

    public ImportResult Import(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("找不到要导入的 XLSX 文件。", filePath);
        if (!string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目前仅支持 .xlsx 文件。请先将旧版 Excel 文件另存为 XLSX。");

        var rows = ParseWorkbook(filePath);
        if (rows.Count == 0) throw new InvalidOperationException("没有识别到期刊数据。请确认文件包含期刊名称、ISSN、等级等表头。");

        var warnings = rows.SelectMany(x => x.Warnings).Distinct().Take(30).ToList();
        var snapshot = HasExistingJournals() ? backups.CreateSnapshot("导入期刊资料前") : null;
        var inserted = 0;
        var updated = 0;
        var skipped = 0;
        try
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var row in rows)
            {
                var result = UpsertRow(connection, transaction, row, filePath);
                inserted += result.Inserted;
                updated += result.Updated;
                skipped += result.Skipped;
            }
            transaction.Commit();
            backups.CommitSnapshot(snapshot);
        }
        catch
        {
            backups.DiscardSnapshot(snapshot);
            throw;
        }

        return new ImportResult
        {
            ReadCount = rows.Count,
            InsertedCount = inserted,
            UpdatedCount = updated,
            SkippedCount = skipped,
            Warnings = warnings
        };
    }

    private bool HasExistingJournals()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Journals LIMIT 1);";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public List<JournalRecord> Search(JournalFilter filter, int limit = 2000)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        var clauses = new List<string> { "1=1" };
        AddLike(command, clauses, "q", filter.Keyword, "(j.Name LIKE $q ESCAPE '\\' OR j.Issn LIKE $q ESCAPE '\\')");
        AddLike(command, clauses, "issn", filter.Issn, "j.Issn LIKE $issn ESCAPE '\\'");
        AddLike(command, clauses, "subject", filter.SubjectArea, "j.SubjectArea LIKE $subject ESCAPE '\\'");
        AddLike(command, clauses, "category", filter.Category, "c.Category LIKE $category ESCAPE '\\'");
        AddExact(command, clauses, "rank", filter.Rank, "c.Rank = $rank");
        AddExact(command, clauses, "quartile", filter.Quartile, "j.Quartile = $quartile");
        if (filter.IsOa is not null)
        {
            clauses.Add("j.IsOa = $oa");
            command.Parameters.AddWithValue("$oa", filter.IsOa.Value ? 1 : 0);
        }
        else if (filter.OaUnknownOnly) clauses.Add("j.IsOa IS NULL");
        AddRange(command, clauses, "ifMin", filter.MinImpactFactor, "j.ImpactFactor >= $ifMin");
        AddRange(command, clauses, "ifMax", filter.MaxImpactFactor, "j.ImpactFactor <= $ifMax");
        AddRange(command, clauses, "articlesMin", filter.MinAnnualArticles, "j.AnnualArticles >= $articlesMin");
        AddRange(command, clauses, "articlesMax", filter.MaxAnnualArticles, "j.AnnualArticles <= $articlesMax");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));
        command.CommandText = $"""
            SELECT c.Id, j.Id, c.SequenceNumber, c.Category, c.Rank, j.Issn, j.Name,
                   j.ImpactFactorText, j.ImpactFactor, j.Quartile, j.SubjectArea,
                   j.OaText, j.IsOa, j.AnnualArticlesText, j.AnnualArticles,
                   j.SourceFile, c.SourceSheet, j.UpdatedAt
            FROM JournalClassifications c
            JOIN Journals j ON j.Id = c.JournalId
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY c.Category, CASE c.Rank WHEN 'A类' THEN 0 WHEN 'B类' THEN 1 ELSE 2 END,
                     c.SequenceNumber, j.Name COLLATE NOCASE
            LIMIT $limit;
            """;
        using var reader = command.ExecuteReader();
        var results = new List<JournalRecord>();
        while (reader.Read()) results.Add(ReadJournal(reader));
        return results;
    }

    public List<JournalLookupRecord> Lookup(string text = "", int limit = 500)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Issn, Name FROM Journals
            WHERE $q = '' OR Name LIKE $like OR Issn LIKE $like
            ORDER BY Name COLLATE NOCASE LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$q", text.Trim());
        command.Parameters.AddWithValue("$like", $"%{EscapeLike(text.Trim())}%");
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<JournalLookupRecord>();
        while (reader.Read()) rows.Add(new JournalLookupRecord { Id = reader.GetInt64(0), Issn = reader.GetString(1), Name = reader.GetString(2) });
        return rows;
    }

    public void UpdateJournal(JournalRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Name)) throw new InvalidOperationException("期刊名称不能为空。");
        var snapshot = backups.CreateSnapshot("修改期刊资料前");
        try
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE Journals SET Issn=$issn, Name=$name, NormalizedKey=$key,
                        ImpactFactorText=$ifText, ImpactFactor=$ifValue, Quartile=$quartile,
                        SubjectArea=$subject, OaText=$oaText, IsOa=$isOa,
                        AnnualArticlesText=$articlesText, AnnualArticles=$articles,
                        UpdatedAt=$now WHERE Id=$id;
                    """;
                command.Parameters.AddWithValue("$issn", record.Issn.Trim());
                command.Parameters.AddWithValue("$name", record.Name.Trim());
                command.Parameters.AddWithValue("$key", BuildKey(record.Issn, record.Name));
                command.Parameters.AddWithValue("$ifText", record.ImpactFactorText.Trim());
                command.Parameters.AddWithValue("$ifValue", ParseDouble(record.ImpactFactorText) is { } d ? d : DBNull.Value);
                command.Parameters.AddWithValue("$quartile", record.Quartile.Trim());
                command.Parameters.AddWithValue("$subject", record.SubjectArea.Trim());
                command.Parameters.AddWithValue("$oaText", record.OaText.Trim());
                command.Parameters.AddWithValue("$isOa", ParseOa(record.OaText) is { } oa ? (oa ? 1 : 0) : DBNull.Value);
                command.Parameters.AddWithValue("$articlesText", record.AnnualArticlesText.Trim());
                command.Parameters.AddWithValue("$articles", ParseInt(record.AnnualArticlesText) is { } i ? i : DBNull.Value);
                command.Parameters.AddWithValue("$now", DatabaseService.Now());
                command.Parameters.AddWithValue("$id", record.JournalId);
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("期刊记录不存在或已被删除。");
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE JournalClassifications SET Rank=$rank, Category=$category, UpdatedAt=$now WHERE Id=$id;";
                command.Parameters.AddWithValue("$rank", record.Rank.Trim());
                command.Parameters.AddWithValue("$category", string.IsNullOrWhiteSpace(record.Category) ? "未分类" : record.Category.Trim());
                command.Parameters.AddWithValue("$now", DatabaseService.Now());
                command.Parameters.AddWithValue("$id", record.Id);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            backups.CommitSnapshot(snapshot);
        }
        catch
        {
            backups.DiscardSnapshot(snapshot);
            throw;
        }
    }

    private static List<ImportRow> ParseWorkbook(string filePath)
    {
        using var workbook = new XLWorkbook(filePath);
        var results = new List<ImportRow>();
        foreach (var sheet in workbook.Worksheets)
        {
            var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
            var category = "未分类";
            Dictionary<string, int>? header = null;
            for (var rowNumber = 1; rowNumber <= last; rowNumber++)
            {
                var values = Enumerable.Range(1, Math.Max(9, sheet.LastColumnUsed()?.ColumnNumber() ?? 9))
                    .Select(col => sheet.Cell(rowNumber, col).GetFormattedString().Trim()).ToArray();
                var nonEmpty = values.Count(v => !string.IsNullOrWhiteSpace(v));
                if (nonEmpty == 1 && !LooksLikeHeader(values))
                {
                    category = values.First(v => !string.IsNullOrWhiteSpace(v));
                    header = null;
                    continue;
                }
                if (LooksLikeHeader(values))
                {
                    header = MapHeader(values);
                    continue;
                }
                if (header is null) continue;
                string Get(params string[] names)
                {
                    foreach (var name in names)
                        if (header.TryGetValue(NormalizeHeader(name), out var index) && index < values.Length) return values[index];
                    return "";
                }
                var name = Get("期刊名称", "期刊名", "journal name", "name", "title");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var issn = Get("ISSN号", "ISSN", "ISSN号码");
                var row = new ImportRow
                {
                    Sheet = sheet.Name,
                    Row = rowNumber,
                    Category = category,
                    SequenceNumber = ParseInt(Get("序号", "编号", "no")) ?? 0,
                    Rank = Get("等级", "类别", "级别", "grade", "class"),
                    Issn = issn,
                    Name = name,
                    ImpactFactorText = Get("IF值", "IF", "影响因子", "impact factor"),
                    Quartile = Get("新锐期刊分区表分区", "分区", "quartile", "zone"),
                    SubjectArea = Get("学科领域", "学科", "领域", "subject", "field"),
                    OaText = Get("是否OA", "OA", "open access"),
                    AnnualArticlesText = Get("年文章数", "年发文量", "annual articles", "year articles")
                };
                if (string.Equals(issn, "会议", StringComparison.OrdinalIgnoreCase)) row.Warnings.Add($"{sheet}!{rowNumber}：会议记录按名称识别。");
                else if (!string.IsNullOrWhiteSpace(issn) && NormalizeIssn(issn) is null) row.Warnings.Add($"{sheet}!{rowNumber}：ISSN“{issn}”格式非标准，按名称识别。");
                results.Add(row);
            }
        }
        return results;
    }

    private static (int Inserted, int Updated, int Skipped) UpsertRow(SqliteConnection connection, SqliteTransaction transaction, ImportRow row, string sourceFile)
    {
        var key = BuildKey(row.Issn, row.Name);
        long journalId;
        var isNew = false;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT Id FROM Journals WHERE NormalizedKey=$key;";
            select.Parameters.AddWithValue("$key", key);
            var value = select.ExecuteScalar();
            if (value is null)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO Journals(NormalizedKey,Issn,Name,ImpactFactorText,ImpactFactor,Quartile,SubjectArea,OaText,IsOa,
                        AnnualArticlesText,AnnualArticles,SourceFile,SourceSheet,ImportedAt,UpdatedAt)
                    VALUES($key,$issn,$name,$ifText,$ifValue,$quartile,$subject,$oaText,$isOa,$articlesText,$articles,
                        $source,$sheet,$now,$now) RETURNING Id;
                    """;
                AddJournalParameters(insert, row, key, sourceFile);
                journalId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
                isNew = true;
            }
            else
            {
                journalId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE Journals SET Issn=$issn,Name=$name,
                        ImpactFactorText=CASE WHEN $ifText='' THEN ImpactFactorText ELSE $ifText END,
                        ImpactFactor=COALESCE($ifValue,ImpactFactor),
                        Quartile=CASE WHEN $quartile='' OR $quartile LIKE '%未检索%' THEN Quartile ELSE $quartile END,
                        SubjectArea=CASE WHEN $subject='' OR $subject LIKE '%未检索%' THEN SubjectArea ELSE $subject END,
                        OaText=CASE WHEN $oaText='' OR $oaText LIKE '%未检索%' THEN OaText ELSE $oaText END,
                        IsOa=COALESCE($isOa,IsOa),
                        AnnualArticlesText=CASE WHEN $articlesText='' THEN AnnualArticlesText ELSE $articlesText END,
                        AnnualArticles=COALESCE($articles,AnnualArticles),
                        SourceFile=$source,SourceSheet=$sheet,UpdatedAt=$now WHERE Id=$id;
                    """;
                AddJournalParameters(update, row, key, sourceFile);
                update.Parameters.AddWithValue("$id", journalId);
                update.ExecuteNonQuery();
            }
        }

        var classificationChanged = false;
        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO JournalClassifications(JournalId,Category,Rank,SequenceNumber,SourceSheet,UpdatedAt)
                VALUES($journal,$category,$rank,$sequence,$sheet,$now)
                ON CONFLICT(JournalId,Category) DO UPDATE SET
                    Rank=excluded.Rank, SequenceNumber=excluded.SequenceNumber,
                    SourceSheet=excluded.SourceSheet, UpdatedAt=excluded.UpdatedAt
                RETURNING Id;
                """;
            upsert.Parameters.AddWithValue("$journal", journalId);
            upsert.Parameters.AddWithValue("$category", string.IsNullOrWhiteSpace(row.Category) ? "未分类" : row.Category);
            upsert.Parameters.AddWithValue("$rank", row.Rank);
            upsert.Parameters.AddWithValue("$sequence", row.SequenceNumber);
            upsert.Parameters.AddWithValue("$sheet", row.Sheet);
            upsert.Parameters.AddWithValue("$now", DatabaseService.Now());
            classificationChanged = upsert.ExecuteScalar() is not null;
        }
        return isNew ? (1, 0, 0) : classificationChanged ? (0, 1, 0) : (0, 0, 1);
    }

    private static void AddJournalParameters(SqliteCommand command, ImportRow row, string key, string sourceFile)
    {
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$issn", row.Issn);
        command.Parameters.AddWithValue("$name", row.Name);
        command.Parameters.AddWithValue("$ifText", row.ImpactFactorText);
        command.Parameters.AddWithValue("$ifValue", ParseDouble(row.ImpactFactorText) is { } d ? d : DBNull.Value);
        command.Parameters.AddWithValue("$quartile", row.Quartile);
        command.Parameters.AddWithValue("$subject", row.SubjectArea);
        command.Parameters.AddWithValue("$oaText", row.OaText);
        command.Parameters.AddWithValue("$isOa", ParseOa(row.OaText) is { } oa ? (oa ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("$articlesText", row.AnnualArticlesText);
        command.Parameters.AddWithValue("$articles", ParseInt(row.AnnualArticlesText) is { } i ? i : DBNull.Value);
        command.Parameters.AddWithValue("$source", Path.GetFileName(sourceFile));
        command.Parameters.AddWithValue("$sheet", row.Sheet);
        command.Parameters.AddWithValue("$now", DatabaseService.Now());
    }

    private static JournalRecord ReadJournal(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0), JournalId = reader.GetInt64(1), SequenceNumber = reader.GetInt32(2),
        Category = reader.GetString(3), Rank = reader.GetString(4), Issn = reader.GetString(5), Name = reader.GetString(6),
        ImpactFactorText = reader.GetString(7), ImpactFactor = reader.IsDBNull(8) ? null : reader.GetDouble(8),
        Quartile = reader.GetString(9), SubjectArea = reader.GetString(10), OaText = reader.GetString(11),
        IsOa = reader.IsDBNull(12) ? null : reader.GetInt32(12) == 1,
        AnnualArticlesText = reader.GetString(13), AnnualArticles = reader.IsDBNull(14) ? null : reader.GetInt32(14),
        SourceFile = reader.GetString(15), SourceSheet = reader.GetString(16), UpdatedAt = reader.GetString(17)
    };

    private static bool LooksLikeHeader(string[] values)
    {
        var normalized = values.Select(NormalizeHeader).ToArray();
        var score = HeaderTokens.Count(token => normalized.Any(x => x.Contains(NormalizeHeader(token), StringComparison.OrdinalIgnoreCase)));
        return score >= 4 && normalized.Any(x => x.Contains("期刊名称")) && normalized.Any(x => x.Contains("issn"));
    }

    private static Dictionary<string, int> MapHeader(string[] values)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < values.Length; i++) if (!string.IsNullOrWhiteSpace(values[i])) map[NormalizeHeader(values[i])] = i;
        return map;
    }

    private static string NormalizeHeader(string value) => Regex.Replace(value, @"[\s_\-：:]", "").ToLowerInvariant();
    private static string NormalizeName(string value) => CollapseSpaceRegex().Replace(value.Normalize(NormalizationForm.FormKC).Trim(), " ").ToUpperInvariant();
    private static string BuildKey(string issn, string name) => NormalizeIssn(issn) is { } normalized ? $"ISSN:{normalized}" : $"NAME:{NormalizeName(name)}";
    private static string? NormalizeIssn(string value)
    {
        var normalized = Regex.Replace(value.Normalize(NormalizationForm.FormKC).ToUpperInvariant(), @"[\s\-]", "");
        return IssnRegex().IsMatch(normalized) && HasValidCheckDigit(normalized) ? normalized : null;
    }
    private static bool HasValidCheckDigit(string issn)
    {
        var sum = 0;
        for (var i = 0; i < 7; i++) sum += (issn[i] - '0') * (8 - i);
        var check = (11 - sum % 11) % 11;
        var expected = check == 10 ? 'X' : (char)('0' + check);
        return issn[7] == expected;
    }
    private static double? ParseDouble(string value) => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result >= 0 ? result : null;
    private static int? ParseInt(string value) => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0 ? result : null;
    private static bool? ParseOa(string value) => value.Trim() switch { "是" => true, "否" => false, _ => null };
    private static void AddLike(SqliteCommand command, List<string> clauses, string name, string value, string clause)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        clauses.Add(clause);
        command.Parameters.AddWithValue("$" + name, $"%{EscapeLike(value.Trim())}%");
    }
    private static void AddExact(SqliteCommand command, List<string> clauses, string name, string value, string clause)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "全部") return;
        clauses.Add(clause);
        command.Parameters.AddWithValue("$" + name, value.Trim());
    }
    private static void AddRange(SqliteCommand command, List<string> clauses, string name, object? value, string clause)
    {
        if (value is null) return;
        clauses.Add(clause);
        command.Parameters.AddWithValue("$" + name, value);
    }
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    [GeneratedRegex(@"^[0-9]{7}[0-9X]$")]
    private static partial Regex IssnRegex();
    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseSpaceRegex();

    private sealed class ImportRow
    {
        public string Sheet { get; init; } = "";
        public int Row { get; init; }
        public string Category { get; init; } = "";
        public int SequenceNumber { get; init; }
        public string Rank { get; init; } = "";
        public string Issn { get; init; } = "";
        public string Name { get; init; } = "";
        public string ImpactFactorText { get; init; } = "";
        public string Quartile { get; init; } = "";
        public string SubjectArea { get; init; } = "";
        public string OaText { get; init; } = "";
        public string AnnualArticlesText { get; init; } = "";
        public List<string> Warnings { get; } = [];
    }
}
