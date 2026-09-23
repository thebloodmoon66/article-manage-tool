using System.Windows;
using System.Windows.Controls;

namespace PaperSubmissionManager.Dialogs;

public sealed class AttachmentImportModeWindow : Window
{
    private bool? _linked;

    private AttachmentImportModeWindow()
    {
        Title = "选择附件添加方式";
        Width = 390;
        Height = 185;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(new TextBlock
        {
            Text = "请选择附件的添加方式：",
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 18)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var importButton = new Button { Content = "导入文件本体", MinWidth = 112, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        importButton.Click += (_, _) => { _linked = false; DialogResult = true; };
        var linkButton = new Button { Content = "只记录文件路径", MinWidth = 112, Margin = new Thickness(0, 0, 8, 0) };
        linkButton.Click += (_, _) => { _linked = true; DialogResult = true; };
        var cancelButton = new Button { Content = "取消", MinWidth = 68, IsCancel = true };
        buttons.Children.Add(importButton);
        buttons.Children.Add(linkButton);
        buttons.Children.Add(cancelButton);
        layout.Children.Add(buttons);
        Content = layout;
    }

    public static bool? Show(Window owner)
    {
        var dialog = new AttachmentImportModeWindow { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._linked : null;
    }
}
