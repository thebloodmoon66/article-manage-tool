using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PaperSubmissionManager.Models;
using PaperSubmissionManager.Services;

var workspace = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new InvalidOperationException("请传入工作区路径。");
var sourceWorkbook = Path.Combine(workspace, "GPT文件", "期刊选择.xlsx");
var testRoot = Path.Combine(workspace, "test-results", "smoke-data");
if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
Directory.CreateDirectory(testRoot);
var paths = new AppPaths(testRoot);
Assert(paths.Root == Path.TrimEndingDirectorySeparator(Path.GetFullPath(testRoot)), "数据根目录必须严格等于用户指定位置。");
Assert(new[] { paths.DatabasePath, paths.AttachmentRoot, paths.BackupRoot }.All(path => IsInside(paths.Root, path)), "所有受管路径必须位于用户指定目录内。");
var blankPathRejected = false; try { _ = new AppPaths(" "); } catch (ArgumentException) { blankPathRejected = true; }
Assert(blankPathRejected, "未选择数据目录时不得回退到用户目录。");
var locationConfig = Path.Combine(testRoot, "portable-config", "数据目录位置.txt");
DataLocationService.Save(testRoot, locationConfig);
Assert(DataLocationService.Load(locationConfig) == paths.Root, "数据目录便携配置保存或读取失败。");
var portableDataRoot = Path.Combine(testRoot, "portable-config", "data"); Directory.CreateDirectory(portableDataRoot);
DataLocationService.Save(portableDataRoot, locationConfig);
Assert(File.ReadAllText(locationConfig).StartsWith("relative:", StringComparison.Ordinal) && DataLocationService.Load(locationConfig) == portableDataRoot, "程序文件夹内的数据目录应以相对路径保存，保证整体搬移后仍可用。");
var db = new DatabaseService(paths); db.Initialize();
var backup = new BackupService(db); var journals = new JournalImportService(db, backup);
var papers = new PaperService(db); var authors = new AuthorService(db); var workspaces = new JournalWorkspaceService(db);

var import = journals.Import(sourceWorkbook);
Assert(import.ReadCount == 418, $"期刊归属应为 418，实际 {import.ReadCount}");
using (var connection = db.OpenConnection())
{
    Assert(Scalar(connection, "SELECT COUNT(*) FROM JournalClassifications") == 417, "重复同学科 GPS Solutions 应按同一归属更新，分类应为 417。");
    Assert(Scalar(connection, "SELECT COUNT(*) FROM Journals") >= 300, "期刊主档数量异常。");
    Assert(Scalar(connection, "SELECT COUNT(*) FROM JournalClassifications c JOIN Journals j ON j.Id=c.JournalId WHERE j.Name='GPS Solutions'") == 1, "同学科完全重复应合并。");
    Assert(Scalar(connection, "SELECT COUNT(*) FROM JournalClassifications c JOIN Journals j ON j.Id=c.JournalId WHERE j.Name='Geophysical Research Letters'") == 3, "跨学科归属不应丢失。");
}
Assert(journals.Search(new JournalFilter { IsOa = true }).Count > 0, "OA 筛选无结果。");
Assert(journals.Search(new JournalFilter { MinImpactFactor = 10, MaxImpactFactor = 11 }).All(x => x.ImpactFactor is >= 10 and <= 11), "IF 闭区间筛选错误。");
Assert(journals.Search(new JournalFilter { Rank = "A类", Quartile = "1区" }).All(x => x.Rank == "A类" && x.Quartile == "1区"), "A类/分区组合筛选错误。");
var unknownOa = journals.Search(new JournalFilter { OaUnknownOnly = true }); Assert(unknownOa.Count > 0 && unknownOa.All(x => x.IsOa is null), "未知/混合OA筛选错误。");

var first = journals.Search(new JournalFilter(), 1)[0]; var originalSubject = first.SubjectArea;
for (var i = 1; i <= 4; i++) { first.SubjectArea = originalSubject + $" / 测试{i}"; journals.UpdateJournal(first); }
Assert(backup.List().Count == 3, "连续四次期刊修改后应只保留三代备份。");

var sampleAttachment = Path.Combine(testRoot, "来源附件.txt"); File.WriteAllText(sampleAttachment, "paper attachment smoke test");
var paperId = papers.CreatePaper("冒烟测试论文", "初始备注", [new PendingAttachment("论文正文", sampleAttachment)]);
papers.UpdatePaper(paperId, "修改后的论文名称", "修改后的备注");
Assert(papers.GetPaper(paperId)?.Name == "修改后的论文名称" && papers.GetPaper(paperId)?.Notes == "修改后的备注", "论文详情中的名称和备注应可一起修改。");
papers.UpdatePaper(paperId, "冒烟测试论文", "初始备注");
File.Delete(sampleAttachment);
var attachment = papers.ListAttachments(paperId).Single();
Assert(File.Exists(papers.ResolveAttachmentPath(attachment)), "删除源文件后受管附件仍应存在。");
Assert(!Path.IsPathRooted(attachment.StoredPath), "附件数据库路径必须是相对路径。");
Assert(Path.GetFileName(papers.ResolveAttachmentPath(attachment)) == "来源附件.txt", "受管附件实体必须保留导入时的原文件名。");
var sameNameSource = Path.Combine(testRoot, "来源附件.txt"); File.WriteAllText(sameNameSource, "same-name second attachment");
var duplicateNameAttachment = papers.AddAttachments(paperId, [new PendingAttachment("论文正文副本", sameNameSource)]).Single(); File.Delete(sameNameSource);
Assert(Path.GetFileName(papers.ResolveAttachmentPath(duplicateNameAttachment)) == "来源附件 (2).txt", "同名附件应追加序号，不能覆盖已有文件。");
var renamedAttachment = papers.RenameAttachmentFile(duplicateNameAttachment.Id, "修改后的附件.txt");
Assert(renamedAttachment.ActualFileName == "修改后的附件.txt" && File.Exists(papers.ResolveAttachmentPath(renamedAttachment)), "附件真实文件名修改失败。");
var renameCollision = papers.RenameAttachmentFile(renamedAttachment.Id, "来源附件.txt");
Assert(renameCollision.ActualFileName == "来源附件 (2).txt", "重命名冲突时应自动追加编号。");
papers.DeleteAttachment(duplicateNameAttachment.Id);
Assert(papers.IsRiskyExtension("run.ps1"), "风险扩展名识别失败。");

var authorId = authors.CreateAuthor("测试作者"); authors.AddEmail(authorId, "one@example.com"); authors.AddEmail(authorId, "two@example.com");
Assert(authors.ListEmails(authorId).Count == 2, "作者多邮箱失败。");
var duplicateRejected = false; try { authors.AddEmail(authorId, "ONE@example.com"); } catch (InvalidOperationException) { duplicateRejected = true; }
Assert(duplicateRejected, "同作者重复邮箱应拒绝。");

var journal = journals.Lookup("Applied Thermal Engineering").First(); var workspaceId = workspaces.CreateFromJournal(journal.Id);
Assert(workspaces.ListAccounts(workspaceId).Count == 0, "新建期刊栏目不应自动生成空白账号栏。");
var legacyEmptyAccountId = workspaces.AddAccount(workspaceId);
Assert(legacyEmptyAccountId > 0 && workspaces.RemoveLegacyEmptyAccounts() == 1 && workspaces.ListAccounts(workspaceId).Count == 0, "升级时应清理旧版自动生成的空白账号栏。");
var accountId = workspaces.AddAccount(workspaceId, "投稿账号", "temporary-user", "temporary-password");
workspaces.UpdateAccount(accountId, "主账号", "submit-user", "S3cret!");
Assert(workspaces.ListAccounts(workspaceId).Single().Password == "S3cret!", "本地密码记录读取失败。");
using (var connection = db.OpenConnection())
{
    using var command = connection.CreateCommand(); command.CommandText = "SELECT PasswordCipher FROM JournalAccounts WHERE Id=$id"; command.Parameters.AddWithValue("$id", accountId);
    var storedPassword = Convert.ToString(command.ExecuteScalar())!; Assert(storedPassword == "S3cret!", "密码应按用户要求以明文保存在本地数据库中。");
}
workspaces.LinkPaper(workspaceId, paperId); Assert(workspaces.ListLinkedPapers(workspaceId).Single().Id == paperId, "期刊到论文关联失败。");
var autoSubmission = papers.ListSubmissions(paperId).Single();
Assert(autoSubmission.JournalWorkspaceId == workspaceId && autoSubmission.CurrentStatus == "未投稿", "从期刊栏目关联论文时应立即生成未投稿状态。");
var submissionId = papers.AddSubmission(paperId, journal.Name, workspaceId); Assert(submissionId == autoSubmission.Id, "已有未投稿同步记录时不应重复创建。"); var note1 = papers.AddSubmissionNote(submissionId, "投稿中", "已投稿"); papers.EditSubmissionNote(note1, "编辑部处理中");
papers.UpdateSubmissionStatus(submissionId, "已投稿");
Assert(papers.ListSubmissions(paperId).Single().CurrentStatus == "已投稿", "投稿状态下拉菜单应能直接把当前状态改为已投稿。");
var versions = papers.ListSubmissionNotes(submissionId); Assert(versions.Count == 2 && versions.Count(x => x.IsCurrent) == 1 && versions.Max(x => x.VersionNumber) == 2, "纪要历史版本追加失败。");
papers.AddSubmissionNote(submissionId, "返修中", "编辑要求返修");
Assert(papers.ListSubmissionNotes(submissionId).First().Content == "编辑要求返修", "新增状态纪要应排列在列表第一行。");
Assert(papers.ListSubmissions(paperId).Single().CurrentStatus == "返修中", "新增纪要必须更新该投稿记录唯一的当前状态。");
var paperWithStatus = papers.ListPapers().Single(x => x.Id == paperId);
Assert(paperWithStatus.LatestJournalName == journal.Name && paperWithStatus.LatestSubmissionStatus == "返修中", "论文列表必须显示最新投稿期刊及其当前状态。");
Assert(papers.ListSubmissions(paperId).Single().JournalWorkspaceId == workspaceId, "论文到期刊跳转关联失败。");
Assert(workspaces.GetJournalRecords(workspaceId).Count > 0, "期刊栏目应实时读取数据库资料。");

var transfer = new DataTransferService(db, backup, papers);
var packagePath = Path.Combine(testRoot, "业务数据导出.psmdata");
var exported = transfer.Export(packagePath);
Assert(exported.PaperCount == 1 && exported.WorkspaceCount == 1 && exported.AuthorCount == 1 && exported.AttachmentCount == 1, "三个板块的业务数据导出统计错误。");
papers.UpdatePaper(paperId, "冒烟测试论文", "本地冲突备注"); authors.RenameAuthor(authorId, "测试作者");
authors.DeleteEmail(authors.ListEmails(authorId).Single(x => x.Email == "two@example.com").Id);
workspaces.UpdateAccount(accountId, "错误账号", "wrong-user", "wrong-password"); workspaces.SaveSubmissionLink(workspaceId, "https://local.invalid/");
var replacementAttachment = Path.Combine(testRoot, "替换来源附件.txt"); File.WriteAllText(replacementAttachment, "local replacement");
papers.DeleteAttachment(attachment.Id); papers.AddAttachments(paperId, [new PendingAttachment("论文正文", replacementAttachment)]); File.Delete(replacementAttachment);
var imported = transfer.Import(packagePath);
Assert(imported.PaperCount == 1 && imported.WorkspaceCount == 1 && papers.GetPaper(paperId)?.Notes == "初始备注", "导入数据应优先覆盖同名论文基础信息。");
var importedAttachment = papers.ListAttachments(paperId).Single(x => x.DisplayName == "论文正文");
Assert(File.ReadAllText(papers.ResolveAttachmentPath(importedAttachment)) == "paper attachment smoke test", "导入数据应优先覆盖同名附件内容。");
Assert(authors.ListEmails(authorId).Select(x => x.Email).Order().SequenceEqual(new[] { "one@example.com", "two@example.com" }), "作者邮箱导入恢复失败。");
Assert(workspaces.ListAccounts(workspaceId).Single().AccountName == "submit-user" && workspaces.Get(workspaceId)?.SubmissionLink == "", "导入数据应优先覆盖同名期刊栏目的账号和链接。");
Assert(workspaces.ListLinkedPapers(workspaceId).Single().Id == paperId && papers.ListSubmissions(paperId).Single().JournalWorkspaceId == workspaceId, "导入后跨板块关联恢复失败。");
var importedAccountId = workspaces.ListAccounts(workspaceId).Single().Id;
workspaces.DeleteAccount(importedAccountId);
Assert(workspaces.ListAccounts(workspaceId).Count == 0, "期刊栏目中的最后一条账号也应允许删除。");

var syncPaperId = papers.CreatePaper("双向同步测试论文");
workspaces.LinkPaper(workspaceId, syncPaperId);
Assert(papers.ListSubmissions(syncPaperId).Single().JournalWorkspaceId == workspaceId, "期刊管理关联后论文投稿状态未实时同步。");
workspaces.UnlinkPaper(workspaceId, syncPaperId);
Assert(papers.ListSubmissions(syncPaperId).Count == 0 && workspaces.ListLinkedPapers(workspaceId).All(x => x.Id != syncPaperId), "期刊管理取消关联后论文投稿状态未同步移除。");
papers.DeletePaper(syncPaperId);
var reverseSyncPaperId = papers.CreatePaper("反向同步测试论文");
var reverseSubmissionId = papers.AddSubmission(reverseSyncPaperId, journal.Name, workspaceId);
Assert(workspaces.ListLinkedPapers(workspaceId).Any(x => x.Id == reverseSyncPaperId), "论文投稿状态新增期刊后期刊栏目未实时关联论文。");
papers.DeleteSubmission(reverseSubmissionId);
Assert(workspaces.ListLinkedPapers(workspaceId).All(x => x.Id != reverseSyncPaperId), "论文投稿状态删除最后一条期刊记录后期刊栏目关联未同步移除。");
papers.DeletePaper(reverseSyncPaperId);

workspaces.Delete(workspaceId);
Assert(workspaces.Get(workspaceId) is null, "期刊栏目删除失败。");
var preservedSubmission = papers.ListSubmissions(paperId).Single();
Assert(preservedSubmission.JournalWorkspaceId is null && preservedSubmission.JournalName == journal.Name, "删除期刊栏目后应保留投稿期刊名称历史，并只解除栏目跳转。 ");
Assert(papers.ListSubmissionNotes(submissionId).Count == 3, "删除期刊栏目后状态纪要及其历史版本必须保留。");
Assert(papers.GetPaper(paperId) is not null && File.Exists(papers.ResolveAttachmentPath(importedAttachment)), "删除期刊栏目不得删除论文或附件。");

using (var connection = db.OpenConnection())
{
    using var integrity = connection.CreateCommand(); integrity.CommandText = "PRAGMA integrity_check"; Assert(Convert.ToString(integrity.ExecuteScalar()) == "ok", "数据库完整性校验失败。");
}

Console.WriteLine($"PASS journals={import.ReadCount}; classifications=417; backups={backup.List().Count}; noteVersions={versions.Count}");

static long Scalar(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static bool IsInside(string root, string candidate)
{
    var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
    var normalizedCandidate = Path.GetFullPath(candidate);
    return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
}
