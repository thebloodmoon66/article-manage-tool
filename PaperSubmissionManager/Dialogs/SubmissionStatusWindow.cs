using System.Windows;
using System.Windows.Controls;
using PaperSubmissionManager.Services;

namespace PaperSubmissionManager.Dialogs;

public sealed class SubmissionStatusWindow : Window
{
    private readonly ComboBox statusBox = new();
    private readonly TextBox contentBox = new();

    public SubmissionStatusWindow(string currentStatus)
    {
        Title = "新增投稿状态";
        Width = 560;
        Height = 400;
        MinWidth = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Add(root, 0, new TextBlock { Text = "投稿状态", Margin = new Thickness(4), FontWeight = FontWeights.SemiBold });
        statusBox.ItemsSource = PaperService.SubmissionStatuses;
        statusBox.SelectedItem = PaperService.SubmissionStatuses.Contains(currentStatus) ? currentStatus : "投稿中";
        Add(root, 1, statusBox);
        Add(root, 2, new TextBlock { Text = "状态纪要（新增后显示在列表第一行）", Margin = new Thickness(4, 12, 4, 4), FontWeight = FontWeights.SemiBold });
        contentBox.AcceptsReturn = true;
        contentBox.TextWrapping = TextWrapping.Wrap;
        contentBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        contentBox.VerticalContentAlignment = VerticalAlignment.Top;
        Add(root, 3, contentBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, MinWidth = 88 });
        var ok = new Button { Content = "新增", IsDefault = true, MinWidth = 88 };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(contentBox.Text))
            {
                MessageBox.Show(this, "请填写状态纪要。", "提示");
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        Add(root, 4, buttons);
        Content = root;
        Loaded += (_, _) => contentBox.Focus();
    }

    public string Status => statusBox.SelectedItem?.ToString() ?? "未投稿";
    public string ContentText => contentBox.Text.Trim();

    private static void Add(Grid grid, int row, UIElement element)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }
}
