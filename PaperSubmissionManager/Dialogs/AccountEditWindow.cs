using System.Windows;
using System.Windows.Controls;
using PaperSubmissionManager.Models;

namespace PaperSubmissionManager.Dialogs;

public sealed class AccountEditWindow : Window
{
    private readonly TextBox _label = new();
    private readonly TextBox _account = new();
    private readonly TextBox _password = new();

    public AccountEditWindow(JournalAccountRecord? account = null)
    {
        Title = account is null ? "新建投稿账号栏" : "修改投稿账号栏";
        Width = 520; Height = 330; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _label.Text = account?.Label ?? "投稿账号"; _account.Text = account?.AccountName ?? ""; _password.Text = account?.Password ?? "";
        var root = new Grid { Margin = new Thickness(18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 4; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? GridLength.Auto : new GridLength(58) });
        Add(root, 0, "栏位名称", _label); Add(root, 1, "账号", _account); Add(root, 2, "密码", _password);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true }); var ok = new Button { Content = "保存", IsDefault = true }; ok.Click += (_, _) => { DialogResult = true; Close(); }; buttons.Children.Add(ok);
        Grid.SetRow(buttons, 3); Grid.SetColumnSpan(buttons, 2); root.Children.Add(buttons); Content = root;
    }
    public string Label => string.IsNullOrWhiteSpace(_label.Text) ? "投稿账号" : _label.Text.Trim();
    public string AccountName => _account.Text.Trim();
    public string Password => _password.Text;
    private static void Add(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
        Grid.SetRow(text, row); Grid.SetRow(control, row); Grid.SetColumn(control, 1); control.Margin = new Thickness(4); control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text); grid.Children.Add(control);
    }
}
