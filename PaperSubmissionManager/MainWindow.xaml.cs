using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PaperSubmissionManager.Dialogs;
using PaperSubmissionManager.Models;
using PaperSubmissionManager.Services;

namespace PaperSubmissionManager;

public partial class MainWindow : Window
{
    private AppPaths _paths = null!;
    private DatabaseService _database = null!;
    private BackupService _backups = null!;
    private JournalImportService _journals = null!;
    private PaperService _papers = null!;
    private AuthorService _authors = null!;
    private JournalWorkspaceService _workspaces = null!;
    private DataTransferService _dataTransfer = null!;
    private bool _ready;
    private bool _loadingSubmissionStatus;
    private JournalAccountRecord? _focusedWorkspaceAccount;
    private AuthorEmailRecord? _focusedAuthorEmail;

    public MainWindow(string dataRoot)
    {
        InitializeComponent();
        _paths = new AppPaths(dataRoot); _database = new DatabaseService(_paths); _database.Initialize();
        _backups = new BackupService(_database); _journals = new JournalImportService(_database, _backups);
        _papers = new PaperService(_database); _authors = new AuthorService(_database); _workspaces = new JournalWorkspaceService(_database);
        _workspaces.RemoveLegacyEmptyAccounts();
        SubmissionStatusBox.ItemsSource = PaperService.SubmissionStatuses;
        _papers.NormalizeLegacyAttachmentFileNames();
        _dataTransfer = new DataTransferService(_database, _backups, _papers);
        DataPathText.Text = $"数据目录：{_paths.Root}";
        ImportEmbeddedJournalDataIfEmpty();
        _ready = true;
        SearchJournals(); RefreshPapers(); RefreshAuthors(); RefreshWorkspaces();
        SetStatus("软件已就绪");
    }

    private void ImportEmbeddedJournalDataIfEmpty()
    {
        if (_journals.Search(new JournalFilter(), 1).Count > 0) return;
        var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("期刊选择.xlsx", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) return;
        var temp = Path.Combine(_paths.Root, "首次导入-期刊选择.xlsx");
        using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)!)
        using (var target = File.Create(temp)) source.CopyTo(target);
        try { _journals.Import(temp); } finally { File.Delete(temp); }
    }

    private void SetStatus(string text) => StatusText.Text = $"{DateTime.Now:HH:mm:ss}  {text}";
    private void ExportBusinessData_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Title = "导出论文、期刊投稿栏目和作者数据",
            Filter = "论文投稿管理数据包 (*.psmdata)|*.psmdata",
            FileName = $"论文投稿数据-{DateTime.Now:yyyyMMdd-HHmmss}.psmdata",
            AddExtension = true,
            DefaultExt = ".psmdata"
        };
        if (picker.ShowDialog(this) != true) return;
        Run("数据导出完成", () =>
        {
            var result = _dataTransfer.Export(picker.FileName);
            MessageBox.Show(this, $"数据导出完成。\n\n论文：{result.PaperCount}\n期刊投稿栏目：{result.WorkspaceCount}\n作者：{result.AuthorCount}\n附件：{result.AttachmentCount}\n\n数据包包含本地明文投稿账号和密码，请妥善保管。", "导出完成");
        });
    }
    private void ImportBusinessData_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "导入论文、期刊投稿栏目和作者数据", Filter = "论文投稿管理数据包 (*.psmdata)|*.psmdata", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        if (!Confirm(this, "导入包中的同名论文、同名作者和同名期刊栏目将优先覆盖本地对应数据；本地其他记录会保留。\n\n导入前会自动备份数据库。是否继续？")) return;
        Run("数据导入完成", () =>
        {
            var result = _dataTransfer.Import(picker.FileName);
            RefreshPapers(); RefreshAuthors(); RefreshWorkspaces(); ClearPaperDetail();
            MessageBox.Show(this, $"数据导入完成。\n\n论文：{result.PaperCount}\n期刊投稿栏目：{result.WorkspaceCount}\n作者：{result.AuthorCount}\n附件：{result.AttachmentCount}\n\n发生冲突时已采用导入包数据。", "导入完成");
        });
    }
    private void DataPathText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var selected = DataLocationService.Choose(this, _paths.Root);
        if (string.IsNullOrWhiteSpace(selected)) return;
        selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
        if (string.Equals(selected, _paths.Root, StringComparison.OrdinalIgnoreCase)) return;
        if (!Confirm(this, $"要把数据目录切换为：\n{selected}\n\n软件将立即重启，但不会自动搬移原目录中的数据。当前尚未点击保存的界面内容不会保留。")) return;

        try
        {
            _ = new AppPaths(selected);
            DataLocationService.Save(selected);
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定软件启动文件路径。");
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"切换数据目录失败：\n{ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void Run(string success, Action action)
    {
        try { Mouse.OverrideCursor = Cursors.Wait; action(); SetStatus(success); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error); SetStatus("操作失败"); }
        finally { Mouse.OverrideCursor = null; }
    }
    private static string ComboText(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? combo.Text;
    private static double? ParseDoubleBox(TextBox box, string label)
    {
        if (string.IsNullOrWhiteSpace(box.Text)) return null;
        if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0) throw new InvalidOperationException($"{label}必须是非负数字。");
        return value;
    }
    private static int? ParseIntBox(TextBox box, string label)
    {
        if (string.IsNullOrWhiteSpace(box.Text)) return null;
        if (!int.TryParse(box.Text.Trim(), out var value) || value < 0) throw new InvalidOperationException($"{label}必须是非负整数。");
        return value;
    }
    private static bool Confirm(Window owner, string text) => MessageBox.Show(owner, text, "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.Source != MainTabs) return;
        switch (MainTabs.SelectedIndex) { case 0: SearchJournals(); break; case 1: RefreshPapers(); break; case 2: RefreshAuthors(); break; case 3: RefreshWorkspaces(); break; }
    }

    private JournalFilter ReadJournalFilter()
    {
        var oaText = ComboText(JournalOaBox);
        var filter = new JournalFilter
        {
            Keyword = JournalKeywordBox.Text, Issn = JournalIssnBox.Text, SubjectArea = JournalSubjectBox.Text, Category = JournalCategoryBox.Text,
            Rank = ComboText(JournalRankBox), Quartile = ComboText(JournalQuartileBox),
            IsOa = oaText == "是" ? true : oaText == "否" ? false : null,
            MinImpactFactor = ParseDoubleBox(IfMinBox, "IF 最小值"), MaxImpactFactor = ParseDoubleBox(IfMaxBox, "IF 最大值"),
            MinAnnualArticles = ParseIntBox(ArticlesMinBox, "年文章数最小值"), MaxAnnualArticles = ParseIntBox(ArticlesMaxBox, "年文章数最大值")
        };
        if (oaText == "未知/混合") filter.OaUnknownOnly = true;
        if (filter.MinImpactFactor > filter.MaxImpactFactor) throw new InvalidOperationException("IF 最小值不能大于最大值。");
        if (filter.MinAnnualArticles > filter.MaxAnnualArticles) throw new InvalidOperationException("年文章数最小值不能大于最大值。");
        return filter;
    }
    private void SearchJournals() { var list = _journals.Search(ReadJournalFilter()); JournalGrid.ItemsSource = list; JournalCountText.Text = $"显示 {list.Count} 条学科归属记录（同一期刊可属于多个学科段；最多显示 2000 条）"; }
    private void SearchJournals_Click(object sender, RoutedEventArgs e) => Run("查询完成", SearchJournals);
    private void JournalFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.FocusedElement is not (TextBox or ComboBox)) return;
        e.Handled = true;
        Run("查询完成", SearchJournals);
    }
    private void ClearJournalFilter_Click(object sender, RoutedEventArgs e)
    {
        JournalKeywordBox.Clear(); JournalIssnBox.Clear(); JournalSubjectBox.Clear(); JournalCategoryBox.Clear(); IfMinBox.Clear(); IfMaxBox.Clear(); ArticlesMinBox.Clear(); ArticlesMaxBox.Clear();
        JournalRankBox.SelectedIndex = 0; JournalQuartileBox.SelectedIndex = 0; JournalOaBox.SelectedIndex = 0; Run("筛选已清空", SearchJournals);
    }
    private void ImportJournals_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "导入期刊资料 XLSX", Filter = "Excel 工作簿 (*.xlsx)|*.xlsx" };
        if (picker.ShowDialog(this) != true) return;
        if (!Confirm(this, "导入会按 ISSN/名称更新期刊数据库，并自动保留导入前备份。是否继续？")) return;
        Run("期刊资料导入完成", () => { var result = _journals.Import(picker.FileName); SearchJournals(); MessageBox.Show(this, $"读取 {result.ReadCount} 条归属记录\n新增 {result.InsertedCount} 个期刊，更新 {result.UpdatedCount} 条归属/期刊数据\n跳过 {result.SkippedCount} 条\n\n警告示例：\n{string.Join("\n", result.Warnings.Take(8))}", "导入结果"); });
    }
    private void EditJournal_Click(object sender, RoutedEventArgs e) => EditSelectedJournal();
    private void JournalGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedJournal();
    private void EditSelectedJournal()
    {
        if (JournalGrid.SelectedItem is not JournalRecord record) { MessageBox.Show(this, "请先选择一条期刊记录。", "提示"); return; }
        var dialog = new JournalEditWindow(record) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Run("期刊修改已保存，修改前版本已备份", () => { _journals.UpdateJournal(dialog.Result); SearchJournals(); RefreshWorkspaces(); });
    }
    private void ShowBackups_Click(object sender, RoutedEventArgs e)
    {
        var list = _backups.List();
        MessageBox.Show(this, list.Count == 0 ? "目前还没有备份。首次导入初始化不占备份代数。" : string.Join("\n", list.Select((x, i) => $"第 {i + 1} 代：{x.ModifiedAt:yyyy-MM-dd HH:mm:ss}  {x.Length / 1024.0:F1} KB\n{x.Name}")), "最近三代数据库备份");
    }

    private void RefreshPapers()
    {
        var selected = (PaperGrid.SelectedItem as PaperRecord)?.Id;
        var expandedIds = (PaperGrid.ItemsSource as IEnumerable<PaperRecord>)?
            .Where(x => x.IsExpanded).Select(x => x.Id).ToHashSet() ?? [];
        var list = _papers.ListPapers();
        foreach (var paper in list) paper.IsExpanded = expandedIds.Contains(paper.Id);
        PaperGrid.ItemsSource = list;
        if (selected is not null) PaperGrid.SelectedItem = list.FirstOrDefault(x => x.Id == selected);
    }
    private void RefreshPapers_Click(object sender, RoutedEventArgs e) => Run("论文列表已刷新", RefreshPapers);
    private void NewPaper_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PaperCreateWindow { Owner = this }; if (dialog.ShowDialog() != true) return;
        Run("论文已创建", () => { var id = _papers.CreatePaper(dialog.PaperName, dialog.Notes, dialog.Attachments); RefreshPapers(); PaperGrid.SelectedItem = (PaperGrid.ItemsSource as IEnumerable<PaperRecord>)?.FirstOrDefault(x => x.Id == id); });
    }
    private void DeletePaper_Click(object sender, RoutedEventArgs e)
    {
        if (PaperGrid.SelectedItem is not PaperRecord paper || !Confirm(this, $"确定删除论文“{paper.Name}”及其投稿历史和受管附件吗？")) return;
        Run("论文已删除", () => { _papers.DeletePaper(paper.Id); RefreshPapers(); ClearPaperDetail(); });
    }
    private void PaperGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSelectedPaper();
    private void PaperGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelectedPaper();
    private void LoadSelectedPaper()
    {
        if (PaperGrid.SelectedItem is not PaperRecord paper) { ClearPaperDetail(); return; }
        var full = _papers.GetPaper(paper.Id) ?? paper; PaperNameBox.Text = full.Name; PaperNotesBox.Text = full.Notes;
        AttachmentGrid.ItemsSource = _papers.ListAttachments(paper.Id); var submissions = _papers.ListSubmissions(paper.Id); SubmissionGrid.ItemsSource = submissions; SubmissionGrid.SelectedIndex = submissions.Count > 0 ? 0 : -1;
    }
    private void ClearPaperDetail()
    {
        PaperNameBox.Clear(); PaperNotesBox.Clear(); AttachmentGrid.ItemsSource = null; SubmissionGrid.ItemsSource = null; SubmissionNoteGrid.ItemsSource = null; SubmissionHeaderText.Text = "尚未记录投稿期刊";
        _loadingSubmissionStatus = true; SubmissionStatusBox.SelectedIndex = -1; SubmissionStatusBox.IsEnabled = false; _loadingSubmissionStatus = false;
    }
    private void SavePaperDetails_Click(object sender, RoutedEventArgs e)
    {
        if (PaperGrid.SelectedItem is not PaperRecord paper) return;
        Run("论文详情已保存", () => { _papers.UpdatePaper(paper.Id, PaperNameBox.Text, PaperNotesBox.Text); RefreshPapers(); RefreshWorkspaces(); LoadWorkspaceDetail(); });
    }
    private void AddPaperAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (PaperGrid.SelectedItem is not PaperRecord paper) { MessageBox.Show(this, "请先选择论文。", "提示"); return; }
        var linked = AttachmentImportModeWindow.Show(this);
        if (linked is null) return;
        var picker = new OpenFileDialog { Multiselect = true, Filter = "所有文件|*.*" }; if (picker.ShowDialog(this) != true) return;
        AddAttachmentFiles(paper, picker.FileNames, linked.Value);
    }
    private void AttachmentArea_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = PaperGrid.SelectedItem is PaperRecord && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void AttachmentArea_Drop(object sender, DragEventArgs e)
    {
        if (PaperGrid.SelectedItem is not PaperRecord paper) { MessageBox.Show(this, "请先选择论文，再拖入附件。", "提示"); return; }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) { MessageBox.Show(this, "请拖入文件；暂不支持直接拖入文件夹。", "提示"); return; }
        var linked = AttachmentImportModeWindow.Show(this);
        if (linked is null) return;
        AddAttachmentFiles(paper, files, linked.Value);
        e.Handled = true;
    }
    private void AddAttachmentFiles(PaperRecord paper, IEnumerable<string> files, bool linked)
    {
        var pending = new List<PendingAttachment>();
        foreach (var file in files)
        {
            var name = TextPromptWindow.Show(this, "附件管理名称", $"请输入“{Path.GetFileName(file)}”的管理名称：", Path.GetFileNameWithoutExtension(file));
            if (!string.IsNullOrWhiteSpace(name)) pending.Add(new PendingAttachment(name, file, linked));
        }
        if (pending.Count == 0) return;
        Run("附件记录已添加", () => { _papers.AddAttachments(paper.Id, pending); LoadSelectedPaper(); RefreshPapers(); });
    }
    private void OpenPaperAttachment_Click(object sender, RoutedEventArgs e) => OpenSelectedAttachment();
    private void AttachmentGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedAttachment();
    private void OpenSelectedAttachment()
    {
        if (AttachmentGrid.SelectedItem is not AttachmentRecord item) return;
        Run("附件打开操作完成", () =>
        {
            var path = _papers.ResolveAttachmentPath(item);
            if (_papers.IsRiskyExtension(item.ActualFileName) && !Confirm(this, "这是可能执行代码的高风险附件。仅在确认来源可信时打开。是否继续？")) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        });
    }
    private void ShowPaperAttachmentInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (AttachmentGrid.SelectedItem is not AttachmentRecord item) return;
        Run("已在文件夹中选中附件", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_papers.ResolveAttachmentPath(item)}\"") { UseShellExecute = true }));
    }
    private void RenamePaperAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (AttachmentGrid.SelectedItem is not AttachmentRecord item) return;
        var name = TextPromptWindow.Show(this, "修改附件文件名", "请输入新的真实文件名（请保留所需扩展名；重名时自动追加编号）：", item.ActualFileName);
        if (string.IsNullOrWhiteSpace(name)) return;
        Run("附件文件名已修改", () => { _papers.RenameAttachmentFile(item.Id, name); LoadSelectedPaper(); });
    }
    private void DeletePaperAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (AttachmentGrid.SelectedItem is not AttachmentRecord item || !Confirm(this, $"确定删除附件“{item.DisplayName}”吗？{(item.IsExternal ? "（仅删除记录，保留源文件）" : "")}")) return;
        Run("附件已删除", () => { _papers.DeleteAttachment(item.Id); LoadSelectedPaper(); RefreshPapers(); });
    }

    private void AddSubmission_Click(object sender, RoutedEventArgs e)
    {
        if (PaperGrid.SelectedItem is not PaperRecord paper) { MessageBox.Show(this, "请先选择论文。", "提示"); return; }
        var options = _workspaces.List(); var selector = new SelectionWindow("选择投稿期刊栏目", "选择已有期刊栏目；取消后可改为只记录自定义名称。", options.Cast<object>(), nameof(JournalWorkspaceRecord.JournalName)) { Owner = this };
        long? workspaceId = null; string? name;
        if (options.Count > 0 && selector.ShowDialog() == true && selector.SelectedItem is JournalWorkspaceRecord workspace) { workspaceId = workspace.Id; name = workspace.JournalName; }
        else name = TextPromptWindow.Show(this, "记录投稿期刊", "请输入投稿期刊名称：");
        if (string.IsNullOrWhiteSpace(name)) return; var chosenId = workspaceId;
        Run("投稿期刊历史已记录", () => { _papers.AddSubmission(paper.Id, name, chosenId); LoadSelectedPaper(); RefreshPapers(); RefreshWorkspaces(); LoadWorkspaceDetail(); });
    }
    private void DeleteSubmission_Click(object sender, RoutedEventArgs e)
    {
        if (SubmissionGrid.SelectedItem is not PaperSubmissionRecord item || !Confirm(this, "确定删除此条投稿历史及其纪要版本吗？")) return;
        Run("投稿历史已删除", () => { _papers.DeleteSubmission(item.Id); LoadSelectedPaper(); RefreshPapers(); RefreshWorkspaces(); LoadWorkspaceDetail(); });
    }
    private void SubmissionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SubmissionGrid.SelectedItem is PaperSubmissionRecord item)
        {
            SubmissionHeaderText.Text = $"{item.JournalName}　{item.RecordedAtDisplay}";
            _loadingSubmissionStatus = true;
            SubmissionStatusBox.IsEnabled = true;
            SubmissionStatusBox.SelectedItem = item.CurrentStatus;
            _loadingSubmissionStatus = false;
            SubmissionNoteGrid.ItemsSource = _papers.ListSubmissionNotes(item.Id);
        }
        else
        {
            SubmissionHeaderText.Text = "尚未记录投稿期刊";
            _loadingSubmissionStatus = true;
            SubmissionStatusBox.SelectedIndex = -1;
            SubmissionStatusBox.IsEnabled = false;
            _loadingSubmissionStatus = false;
            SubmissionNoteGrid.ItemsSource = null;
        }
    }
    private void SubmissionStatusBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loadingSubmissionStatus || SubmissionGrid.SelectedItem is not PaperSubmissionRecord item || SubmissionStatusBox.SelectedItem is not string status || status == item.CurrentStatus) return;
        Run("投稿状态已更新", () => { _papers.UpdateSubmissionStatus(item.Id, status); RefreshPapers(); RefreshSelectedSubmission(item.Id); });
    }
    private void AddSubmissionNote_Click(object sender, RoutedEventArgs e)
    {
        if (SubmissionGrid.SelectedItem is not PaperSubmissionRecord item) { MessageBox.Show(this, "请先在下拉框选择投稿期刊。", "提示"); return; }
        var dialog = new SubmissionStatusWindow(item.CurrentStatus) { Owner = this }; if (dialog.ShowDialog() != true) return;
        Run("投稿状态已更新", () => { _papers.AddSubmissionNote(item.Id, dialog.Status, dialog.ContentText); RefreshPapers(); RefreshSelectedSubmission(item.Id); });
    }
    private void EditSubmissionNote_Click(object sender, RoutedEventArgs e)
    {
        if (SubmissionNoteGrid.SelectedItem is not SubmissionNoteRecord item) return;
        var content = TextPromptWindow.Show(this, "编辑投稿纪要", "保存后会生成一个新版本，旧版本继续保留：", item.Content, true); if (string.IsNullOrWhiteSpace(content)) return;
        Run("投稿纪要新版本已保存", () => { _papers.EditSubmissionNote(item.Id, content); if (SubmissionGrid.SelectedItem is PaperSubmissionRecord submission) SubmissionNoteGrid.ItemsSource = _papers.ListSubmissionNotes(submission.Id); });
    }
    private void DeleteSubmissionNote_Click(object sender, RoutedEventArgs e)
    {
        if (SubmissionNoteGrid.SelectedItem is not SubmissionNoteRecord item || !Confirm(this, "确定删除这一组纪要的全部历史版本吗？")) return;
        Run("纪要版本组已删除", () => { _papers.DeleteSubmissionNoteHistory(item.VersionGroupId); if (SubmissionGrid.SelectedItem is PaperSubmissionRecord submission) SubmissionNoteGrid.ItemsSource = _papers.ListSubmissionNotes(submission.Id); });
    }
    private void OpenSubmissionWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (SubmissionGrid.SelectedItem is not PaperSubmissionRecord item) return;
        if (item.JournalWorkspaceId is null) { MessageBox.Show(this, "这条历史只记录了期刊名称，尚未关联期刊投稿栏目。", "提示"); return; }
        SelectWorkspace(item.JournalWorkspaceId.Value);
    }
    private void RefreshSelectedSubmission(long submissionId)
    {
        if (PaperGrid.SelectedItem is not PaperRecord paper) return;
        var submissions = _papers.ListSubmissions(paper.Id);
        SubmissionGrid.ItemsSource = submissions;
        SubmissionGrid.SelectedItem = submissions.FirstOrDefault(x => x.Id == submissionId);
    }

    private void RefreshAuthors() { var selected = (AuthorGrid.SelectedItem as AuthorRecord)?.Id; var list = _authors.ListAuthors(); AuthorGrid.ItemsSource = list; if (selected is not null) AuthorGrid.SelectedItem = list.FirstOrDefault(x => x.Id == selected); }
    private void RefreshAuthors_Click(object sender, RoutedEventArgs e) => Run("作者列表已刷新", RefreshAuthors);
    private void NewAuthor_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Show(this, "新建作者", "请输入作者名称："); if (string.IsNullOrWhiteSpace(name)) return;
        Run("作者已创建", () => { var id = _authors.CreateAuthor(name); RefreshAuthors(); AuthorGrid.SelectedItem = (_authors.ListAuthors()).FirstOrDefault(x => x.Id == id); });
    }
    private void DeleteAuthor_Click(object sender, RoutedEventArgs e)
    {
        if (AuthorGrid.SelectedItem is not AuthorRecord author || !Confirm(this, $"确定删除作者“{author.Name}”及其邮箱吗？")) return;
        Run("作者已删除", () => { _authors.DeleteAuthor(author.Id); RefreshAuthors(); AuthorNameBox.Clear(); AuthorEmailGrid.ItemsSource = null; });
    }
    private void AuthorGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { _focusedAuthorEmail = null; if (AuthorGrid.SelectedItem is AuthorRecord author) { AuthorNameBox.Text = author.Name; AuthorEmailGrid.ItemsSource = _authors.ListEmails(author.Id); } }
    private void SaveAuthorName_Click(object sender, RoutedEventArgs e) { if (AuthorGrid.SelectedItem is AuthorRecord author) Run("作者名称已保存", () => { _authors.RenameAuthor(author.Id, AuthorNameBox.Text); RefreshAuthors(); }); }
    private void AddAuthorEmail_Click(object sender, RoutedEventArgs e)
    {
        if (AuthorGrid.SelectedItem is not AuthorRecord author) return; var email = TextPromptWindow.Show(this, "增加邮箱栏", "请输入邮箱："); if (string.IsNullOrWhiteSpace(email)) return;
        Run("邮箱已添加", () => { _authors.AddEmail(author.Id, email); _focusedAuthorEmail = null; AuthorEmailGrid.ItemsSource = _authors.ListEmails(author.Id); RefreshAuthors(); });
    }
    private void EditAuthorEmail_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAuthorEmail() is not { } item) { MessageBox.Show(this, "请先选择要修改的邮箱。", "提示"); return; } var email = TextPromptWindow.Show(this, "修改邮箱", "请输入邮箱：", item.Email); if (string.IsNullOrWhiteSpace(email)) return;
        Run("邮箱已修改", () => { _authors.UpdateEmail(item.Id, email); _focusedAuthorEmail = null; if (AuthorGrid.SelectedItem is AuthorRecord author) AuthorEmailGrid.ItemsSource = _authors.ListEmails(author.Id); });
    }
    private void DeleteAuthorEmail_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAuthorEmail() is not { } item) { MessageBox.Show(this, "请先选择要删除的邮箱。", "提示"); return; }
        if (!Confirm(this, $"确定删除邮箱“{item.Email}”吗？")) return;
        Run("邮箱已删除", () => { _authors.DeleteEmail(item.Id); _focusedAuthorEmail = null; if (AuthorGrid.SelectedItem is AuthorRecord author) { AuthorEmailGrid.ItemsSource = _authors.ListEmails(author.Id); RefreshAuthors(); } });
    }

    private void RefreshWorkspaces() { var selected = (WorkspaceGrid.SelectedItem as JournalWorkspaceRecord)?.Id; var list = _workspaces.List(); WorkspaceGrid.ItemsSource = list; if (selected is not null) WorkspaceGrid.SelectedItem = list.FirstOrDefault(x => x.Id == selected); }
    private void RefreshWorkspaces_Click(object sender, RoutedEventArgs e) => Run("期刊栏目已刷新", RefreshWorkspaces);
    private void NewWorkspaceFromJournal_Click(object sender, RoutedEventArgs e)
    {
        var journals = _journals.Lookup(limit: 2000); if (journals.Count == 0) { MessageBox.Show(this, "请先导入期刊资料。", "提示"); return; }
        var selector = new SelectionWindow("从数据库新建期刊栏目", "搜索并选择期刊：", journals.Cast<object>(), nameof(JournalLookupRecord.DisplayName), item => item is JournalLookupRecord journal ? $"{journal.Name} {journal.Issn}" : "") { Owner = this }; if (selector.ShowDialog() != true || selector.SelectedItem is not JournalLookupRecord journal) return;
        Run("期刊栏目已创建", () => { var id = _workspaces.CreateFromJournal(journal.Id); RefreshWorkspaces(); SelectWorkspaceInGrid(id); });
    }
    private void NewCustomWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Show(this, "自定义新建期刊栏目", "请输入期刊名称（创建后不可修改）："); if (string.IsNullOrWhiteSpace(name)) return;
        Run("自定义期刊栏目已创建", () => { var id = _workspaces.CreateCustom(name); RefreshWorkspaces(); SelectWorkspaceInGrid(id); });
    }
    private void DeleteWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceGrid.SelectedItem is not JournalWorkspaceRecord item) return;
        var message = $"确定删除期刊栏目“{item.JournalName}”吗？\n\n该栏目的投稿链接、账号和论文关联会被删除；论文、附件、投稿期刊历史及状态纪要会保留。";
        if (!Confirm(this, message)) return;
        Run("期刊栏目已删除", () => { _workspaces.Delete(item.Id); RefreshWorkspaces(); LoadWorkspaceDetail(); RefreshPapers(); });
    }
    private void WorkspaceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadWorkspaceDetail();
    private void LoadWorkspaceDetail()
    {
        _focusedWorkspaceAccount = null;
        if (WorkspaceGrid.SelectedItem is not JournalWorkspaceRecord item) { WorkspaceNameBox.Clear(); WorkspaceLinkBox.Clear(); WorkspaceAccountGrid.ItemsSource = null; WorkspacePaperGrid.ItemsSource = null; WorkspaceJournalDataGrid.ItemsSource = null; return; }
        var current = _workspaces.Get(item.Id) ?? item; WorkspaceNameBox.Text = current.JournalName; WorkspaceLinkBox.Text = current.SubmissionLink;
        WorkspaceAccountGrid.ItemsSource = _workspaces.ListAccounts(item.Id); WorkspacePaperGrid.ItemsSource = _workspaces.ListLinkedPapers(item.Id); WorkspaceJournalDataGrid.ItemsSource = _workspaces.GetJournalRecords(item.Id);
    }
    private void SaveWorkspaceLink_Click(object sender, RoutedEventArgs e) { if (WorkspaceGrid.SelectedItem is JournalWorkspaceRecord item) Run("投稿链接已保存", () => { _workspaces.SaveSubmissionLink(item.Id, WorkspaceLinkBox.Text); RefreshWorkspaces(); LoadWorkspaceDetail(); }); }
    private void OpenWorkspaceLink_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(WorkspaceLinkBox.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) { MessageBox.Show(this, "请先填写有效的 http/https 投稿链接。", "提示"); return; }
        Run("已在浏览器打开投稿链接", () => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
    }
    private void AddWorkspaceAccount_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceGrid.SelectedItem is not JournalWorkspaceRecord item) return; var dialog = new AccountEditWindow { Owner = this }; if (dialog.ShowDialog() != true) return;
        Run("账号栏已添加", () => { _workspaces.AddAccount(item.Id, dialog.Label, dialog.AccountName, dialog.Password); LoadWorkspaceDetail(); RefreshWorkspaces(); });
    }
    private void EditWorkspaceAccount_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkspaceAccount() is not { } account) { MessageBox.Show(this, "请先选择要修改的账号栏。", "提示"); return; } var dialog = new AccountEditWindow(account) { Owner = this }; if (dialog.ShowDialog() != true) return;
        Run("账号栏已保存", () => { _workspaces.UpdateAccount(account.Id, dialog.Label, dialog.AccountName, dialog.Password); LoadWorkspaceDetail(); });
    }
    private void DeleteWorkspaceAccount_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkspaceAccount() is not { } account) { MessageBox.Show(this, "请先选择要删除的账号栏。", "提示"); return; }
        if (!Confirm(this, "确定删除这组账号和密码吗？")) return;
        Run("账号栏已删除", () => { _workspaces.DeleteAccount(account.Id); LoadWorkspaceDetail(); RefreshWorkspaces(); });
    }
    private void LinkWorkspacePaper_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceGrid.SelectedItem is not JournalWorkspaceRecord workspace) return; var papers = _papers.ListPapers(); var selector = new SelectionWindow("关联投稿论文", "选择要关联的论文：", papers.Cast<object>(), nameof(PaperRecord.Name)) { Owner = this }; if (selector.ShowDialog() != true || selector.SelectedItem is not PaperRecord paper) return;
        Run("论文与期刊已双向关联", () => { _workspaces.LinkPaper(workspace.Id, paper.Id); LoadWorkspaceDetail(); RefreshWorkspaces(); RefreshPapers(); if (PaperGrid.SelectedItem is PaperRecord) LoadSelectedPaper(); });
    }
    private void UnlinkWorkspacePaper_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceGrid.SelectedItem is not JournalWorkspaceRecord workspace || WorkspacePaperGrid.SelectedItem is not PaperRecord paper || !Confirm(this, "确定取消该论文与期刊栏目的关联吗？\n\n论文投稿状态中该期刊对应的投稿记录和纪要也会同步删除。")) return;
        Run("关联已取消", () => { _workspaces.UnlinkPaper(workspace.Id, paper.Id); LoadWorkspaceDetail(); RefreshWorkspaces(); RefreshPapers(); if (PaperGrid.SelectedItem is PaperRecord) LoadSelectedPaper(); });
    }
    private void OpenWorkspacePaper_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspacePaperGrid.SelectedItem is not PaperRecord paper) return;
        SelectPaper(paper.Id);
    }

    private void CopyableCellTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case JournalAccountRecord account: _focusedWorkspaceAccount = account; break;
            case AuthorEmailRecord email: _focusedAuthorEmail = email; break;
        }
    }

    private JournalAccountRecord? SelectedWorkspaceAccount() =>
        _focusedWorkspaceAccount ??
        WorkspaceAccountGrid.SelectedCells.Select(cell => cell.Item).OfType<JournalAccountRecord>().FirstOrDefault() ??
        WorkspaceAccountGrid.CurrentItem as JournalAccountRecord ??
        WorkspaceAccountGrid.SelectedItem as JournalAccountRecord;

    private AuthorEmailRecord? SelectedAuthorEmail() =>
        _focusedAuthorEmail ??
        AuthorEmailGrid.CurrentItem as AuthorEmailRecord ??
        AuthorEmailGrid.SelectedItem as AuthorEmailRecord;

    private void SelectWorkspace(long workspaceId)
    {
        MainTabs.SelectedIndex = 3;
        RefreshWorkspaces();
        if (!SelectWorkspaceInGrid(workspaceId))
            MessageBox.Show(this, "对应的期刊栏目已不存在。", "提示");
    }

    private bool SelectWorkspaceInGrid(long workspaceId)
    {
        var target = (WorkspaceGrid.ItemsSource as IEnumerable<JournalWorkspaceRecord>)?.FirstOrDefault(x => x.Id == workspaceId);
        if (target is null) return false;
        WorkspaceGrid.SelectedItem = target;
        WorkspaceGrid.UpdateLayout();
        WorkspaceGrid.ScrollIntoView(target);
        LoadWorkspaceDetail();
        return true;
    }

    private void SelectPaper(long paperId)
    {
        MainTabs.SelectedIndex = 1;
        RefreshPapers();
        var target = (PaperGrid.ItemsSource as IEnumerable<PaperRecord>)?.FirstOrDefault(x => x.Id == paperId);
        if (target is null) { MessageBox.Show(this, "对应的论文已不存在。", "提示"); return; }
        PaperGrid.SelectedItem = target;
        PaperGrid.UpdateLayout();
        PaperGrid.ScrollIntoView(target);
        LoadSelectedPaper();
    }
}
