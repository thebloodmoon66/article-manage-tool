using System.Windows;
using System.Windows.Controls;

namespace PaperSubmissionManager.Dialogs;

public sealed class ProcessedPromptWindow : Window
{
    private ProcessedPromptWindow(string prompt)
    {
        Title = "处理后的提示词";
        Width = 760;
        Height = 600;
        MinWidth = 520;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock
        {
            Text = "复制下面的提示词交给 AI 处理；将 AI 返回的 <comment> 结果粘贴到“导入意见”窗口。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var promptBox = new TextBox
        {
            Text = prompt,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(promptBox, 1);
        layout.Children.Add(promptBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var copy = new Button { Content = "复制提示词", MinWidth = 110 };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(prompt); }
            catch (Exception ex) { MessageBox.Show(this, $"复制失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        var close = new Button { Content = "关闭", MinWidth = 80, IsCancel = true };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);
        Content = layout;
    }

    public static void Show(Window owner, string prompt) => new ProcessedPromptWindow(prompt) { Owner = owner }.ShowDialog();
}
