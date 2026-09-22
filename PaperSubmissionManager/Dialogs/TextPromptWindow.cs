using System.Windows;
using System.Windows.Controls;

namespace PaperSubmissionManager.Dialogs;

public sealed class TextPromptWindow : Window
{
    private readonly TextBox _box;

    public TextPromptWindow(string title, string label, string initialValue = "", bool multiline = false)
    {
        Title = title;
        Width = 520;
        Height = multiline ? 360 : 210;
        MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = multiline ? ResizeMode.CanResize : ResizeMode.NoResize;
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(4), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        _box = new TextBox
        {
            Text = initialValue,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        Grid.SetRow(_box, 1);
        root.Children.Add(_box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 88 };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public string Value => _box.Text.Trim();

    public static string? Show(Window owner, string title, string label, string initialValue = "", bool multiline = false)
    {
        var dialog = new TextPromptWindow(title, label, initialValue, multiline) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
