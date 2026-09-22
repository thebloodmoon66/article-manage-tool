using System.Windows;
using System.Windows.Controls;
using PaperSubmissionManager.Models;

namespace PaperSubmissionManager.Dialogs;

public sealed class JournalEditWindow : Window
{
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly JournalRecord _record;

    public JournalEditWindow(JournalRecord source)
    {
        _record = source;
        Title = $"修改期刊 — {source.Name}";
        Width = 660; Height = 690; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        form.ColumnDefinitions.Add(new ColumnDefinition());
        Add(form, "学科段", source.Category);
        Add(form, "等级", source.Rank);
        Add(form, "ISSN", source.Issn);
        Add(form, "期刊名称", source.Name);
        Add(form, "IF 原始值", source.ImpactFactorText);
        Add(form, "分区", source.Quartile);
        Add(form, "学科领域", source.SubjectArea, 95);
        Add(form, "OA 原始值", source.OaText);
        Add(form, "年文章数原始值", source.AnnualArticlesText);
        root.Children.Add(form);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var save = new Button { Content = "保存并备份", IsDefault = true, MinWidth = 120 };
        save.Click += (_, _) => { Apply(); DialogResult = true; Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(save); Grid.SetRow(buttons, 1); root.Children.Add(buttons);
        Content = root;
    }

    public JournalRecord Result => _record;

    private void Add(Grid grid, string label, string value, double height = 38)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
        var box = new TextBox { Text = value, VerticalContentAlignment = VerticalAlignment.Center };
        if (height > 50) { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.VerticalContentAlignment = VerticalAlignment.Top; }
        Grid.SetRow(text, row); Grid.SetRow(box, row); Grid.SetColumn(box, 1);
        grid.Children.Add(text); grid.Children.Add(box); _fields[label] = box;
    }

    private void Apply()
    {
        if (string.IsNullOrWhiteSpace(_fields["期刊名称"].Text)) throw new InvalidOperationException("期刊名称不能为空。");
        _record.Category = _fields["学科段"].Text.Trim(); _record.Rank = _fields["等级"].Text.Trim();
        _record.Issn = _fields["ISSN"].Text.Trim(); _record.Name = _fields["期刊名称"].Text.Trim();
        _record.ImpactFactorText = _fields["IF 原始值"].Text.Trim(); _record.Quartile = _fields["分区"].Text.Trim();
        _record.SubjectArea = _fields["学科领域"].Text.Trim(); _record.OaText = _fields["OA 原始值"].Text.Trim();
        _record.AnnualArticlesText = _fields["年文章数原始值"].Text.Trim();
    }
}
