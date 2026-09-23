using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PaperSubmissionManager.Models;

namespace PaperSubmissionManager.Dialogs;

public sealed class PaperCreateWindow : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 75 };
    private readonly ListBox _attachments = new() { DisplayMemberPath = nameof(PendingAttachment.DisplayName), Height = 180 };
    private readonly List<PendingAttachment> _items = [];

    public PaperCreateWindow()
    {
        Title = "新建论文并导入附件"; Width = 680; Height = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "论文名称", FontWeight = FontWeights.SemiBold }); root.Children.Add(_name);
        root.Children.Add(new TextBlock { Text = "备注" }); root.Children.Add(_notes);
        var bar = new DockPanel(); bar.Children.Add(new TextBlock { Text = "附件（论文文件和自定义文件均可）", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var add = new Button { Content = "选择文件" }; add.Click += Add_Click; var remove = new Button { Content = "移除选中" }; remove.Click += (_, _) => { if (_attachments.SelectedItem is PendingAttachment item) { _items.Remove(item); Refresh(); } };
        actions.Children.Add(add); actions.Children.Add(remove); DockPanel.SetDock(actions, Dock.Right); bar.Children.Add(actions); root.Children.Add(bar); root.Children.Add(_attachments);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true }); var create = new Button { Content = "创建", IsDefault = true }; create.Click += (_, _) => { if (string.IsNullOrWhiteSpace(_name.Text)) { MessageBox.Show(this, "请输入论文名称。", "提示"); return; } DialogResult = true; Close(); }; buttons.Children.Add(create); root.Children.Add(buttons); Content = root;
    }
    public string PaperName => _name.Text.Trim(); public string Notes => _notes.Text.Trim(); public IReadOnlyList<PendingAttachment> Attachments => _items;
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Multiselect = true, Title = "选择论文或自定义附件", Filter = "所有文件|*.*" };
        if (picker.ShowDialog(this) != true) return;
        var linked = AttachmentImportModeWindow.Show(this);
        if (linked is null) return;
        foreach (var file in picker.FileNames)
        {
            var display = TextPromptWindow.Show(this, "附件管理名称", $"请输入“{System.IO.Path.GetFileName(file)}”在软件中的管理名称：", System.IO.Path.GetFileNameWithoutExtension(file));
            if (display is not null && display.Length > 0) _items.Add(new PendingAttachment(display, file, linked.Value));
        }
        Refresh();
    }
    private void Refresh() { _attachments.ItemsSource = null; _attachments.ItemsSource = _items; }
}
