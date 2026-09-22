using System.IO;

namespace PaperSubmissionManager.Models;

public sealed class JournalRecord
{
    public long Id { get; set; }
    public long JournalId { get; set; }
    public int SequenceNumber { get; set; }
    public string Category { get; set; } = "";
    public string Rank { get; set; } = "";
    public string Issn { get; set; } = "";
    public string Name { get; set; } = "";
    public string ImpactFactorText { get; set; } = "";
    public double? ImpactFactor { get; set; }
    public string Quartile { get; set; } = "";
    public string SubjectArea { get; set; } = "";
    public string OaText { get; set; } = "";
    public bool? IsOa { get; set; }
    public string AnnualArticlesText { get; set; } = "";
    public int? AnnualArticles { get; set; }
    public string SourceFile { get; set; } = "";
    public string SourceSheet { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class JournalLookupRecord
{
    public long Id { get; set; }
    public string Issn { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName => string.IsNullOrWhiteSpace(Issn) ? Name : $"{Name}（{Issn}）";
}

public sealed class JournalFilter
{
    public string Keyword { get; set; } = "";
    public string Issn { get; set; } = "";
    public string SubjectArea { get; set; } = "";
    public string Category { get; set; } = "";
    public string Rank { get; set; } = "";
    public string Quartile { get; set; } = "";
    public bool? IsOa { get; set; }
    public bool OaUnknownOnly { get; set; }
    public double? MinImpactFactor { get; set; }
    public double? MaxImpactFactor { get; set; }
    public int? MinAnnualArticles { get; set; }
    public int? MaxAnnualArticles { get; set; }
}

public sealed class PaperRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public int AttachmentCount { get; set; }
    public int SubmissionCount { get; set; }
    public string LatestJournalName { get; set; } = "";
    public string LatestSubmissionStatus { get; set; } = "";
    public bool IsExpanded { get; set; }
    public string LatestJournalDisplay => string.IsNullOrWhiteSpace(LatestJournalName) ? "尚未记录" : LatestJournalName;
    public string LatestSubmissionStatusDisplay => string.IsNullOrWhiteSpace(LatestSubmissionStatus) ? "尚未记录" : LatestSubmissionStatus;
    public string CreatedAtDisplay => DateTimeOffset.TryParse(CreatedAt, out var value)
        ? value.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : CreatedAt;
}

public sealed class AttachmentRecord
{
    public long Id { get; set; }
    public long PaperId { get; set; }
    public string DisplayName { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string StoredPath { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string ActualFileName => Path.GetFileName(StoredPath.Replace('/', Path.DirectorySeparatorChar));
}

public sealed class PaperSubmissionRecord
{
    public long Id { get; set; }
    public long PaperId { get; set; }
    public long? JournalWorkspaceId { get; set; }
    public string JournalName { get; set; } = "";
    public string RecordedAt { get; set; } = "";
    public string CurrentStatus { get; set; } = "未投稿";
    public int NoteCount { get; set; }
    public string RecordedAtDisplay => DateTimeOffset.TryParse(RecordedAt, out var value)
        ? value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
        : RecordedAt;
}

public sealed class SubmissionNoteRecord
{
    public long Id { get; set; }
    public long PaperSubmissionId { get; set; }
    public string VersionGroupId { get; set; } = "";
    public int VersionNumber { get; set; }
    public string Content { get; set; } = "";
    public string RecordedAt { get; set; } = "";
    public bool IsCurrent { get; set; }
}

public sealed class AuthorRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public int EmailCount { get; set; }
}

public sealed class AuthorEmailRecord
{
    public long Id { get; set; }
    public long AuthorId { get; set; }
    public string Email { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

public sealed class JournalWorkspaceRecord
{
    public long Id { get; set; }
    public long? JournalId { get; set; }
    public string JournalName { get; set; } = "";
    public string SubmissionLink { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public int PaperCount { get; set; }
    public int AccountCount { get; set; }
}

public sealed class JournalAccountRecord
{
    public long Id { get; set; }
    public long JournalWorkspaceId { get; set; }
    public string Label { get; set; } = "投稿账号";
    public string AccountName { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class BackupRecord
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public DateTime ModifiedAt { get; init; }
    public long Length { get; init; }
}

public sealed class ImportResult
{
    public int ReadCount { get; init; }
    public int InsertedCount { get; init; }
    public int UpdatedCount { get; init; }
    public int SkippedCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record PendingAttachment(string DisplayName, string SourcePath);
