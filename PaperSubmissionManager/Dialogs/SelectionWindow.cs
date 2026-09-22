using System.Windows;
using System.Windows.Controls;

namespace PaperSubmissionManager.Dialogs;

public sealed class SelectionWindow : Window
{
    private readonly ListBox _list;
    private readonly List<object> _allItems;
    private readonly Func<object, string>? _searchTextSelector;
    private readonly TextBox? _searchBox;
    private readonly TextBlock? _resultCount;

    public SelectionWindow(string title, string label, IEnumerable<object> items, string displayMemberPath, Func<object, string>? searchTextSelector = null)
    {
        _allItems = items.ToList();
        _searchTextSelector = searchTextSelector;
        Title = title; Width = 680; Height = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (searchTextSelector is not null) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4) });
        _list = new ListBox { ItemsSource = _allItems, DisplayMemberPath = displayMemberPath, Margin = new Thickness(4) };
        var listRow = 1;
        if (searchTextSelector is not null)
        {
            var searchPanel = new DockPanel { Margin = new Thickness(4, 6, 4, 6) };
            _resultCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0), Foreground = System.Windows.Media.Brushes.DimGray };
            DockPanel.SetDock(_resultCount, Dock.Right); searchPanel.Children.Add(_resultCount);
            _searchBox = new TextBox { MinHeight = 34, ToolTip = "输入期刊名称或 ISSN；按 Enter 选择当前第一项" };
            _searchBox.TextChanged += (_, _) => ApplyFilter();
            _searchBox.KeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter) return;
                if (_list.SelectedItem is null && _list.Items.Count > 0) _list.SelectedIndex = 0;
                Accept(); e.Handled = true;
            };
            searchPanel.Children.Add(_searchBox); Grid.SetRow(searchPanel, 1); root.Children.Add(searchPanel); listRow = 2;
        }
        _list.MouseDoubleClick += (_, _) => Accept(); Grid.SetRow(_list, listRow); root.Children.Add(_list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true }); var ok = new Button { Content = "选择", IsDefault = true }; ok.Click += (_, _) => Accept(); buttons.Children.Add(ok);
        Grid.SetRow(buttons, listRow + 1); root.Children.Add(buttons); Content = root;
        if (_searchBox is not null) Loaded += (_, _) => { ApplyFilter(); _searchBox.Focus(); };
    }
    public object? SelectedItem => _list.SelectedItem;
    private void Accept() { if (_list.SelectedItem is null) return; DialogResult = true; Close(); }
    private void ApplyFilter()
    {
        if (_searchBox is null || _searchTextSelector is null) return;
        var terms = _searchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filtered = terms.Length == 0
            ? _allItems
            : _allItems.Where(item => terms.All(term => _searchTextSelector(item).Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
        _list.ItemsSource = filtered;
        _list.SelectedIndex = filtered.Count > 0 ? 0 : -1;
        if (_resultCount is not null) _resultCount.Text = $"{filtered.Count} 项";
    }
}
