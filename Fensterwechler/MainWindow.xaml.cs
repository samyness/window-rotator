using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Fensterwechler;

public partial class MainWindow : Window
{
    const string WindowFormat = "WWR.Window";
    const string EntryFormat = "WWR.Entry";

    Point dragStart;
    object? dragItem;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Window Rotator {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} · Daten: {AppPaths.Roaming}";
        // Coming back from the tray: the window list is probably outdated.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Vm.RefreshLibrary(); };
    }

    MainViewModel Vm => (MainViewModel)DataContext;

    // ---- Drag source (both lists) ----

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(null);
        dragItem = IsInteractive(e.OriginalSource as DependencyObject) ? null : ItemAt(e.OriginalSource as DependencyObject)?.DataContext;
    }

    void LibraryList_PreviewMouseMove(object sender, MouseEventArgs e) => StartDrag(sender, e, WindowFormat, DragDropEffects.Copy);

    void RotationList_PreviewMouseMove(object sender, MouseEventArgs e) => StartDrag(sender, e, EntryFormat, DragDropEffects.Move);

    void StartDrag(object sender, MouseEventArgs e, string format, DragDropEffects effects)
    {
        if (dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = dragStart - e.GetPosition(null);
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = dragItem;
        dragItem = null;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(format, item), effects); }
        finally { DropLine.Visibility = Visibility.Collapsed; }
    }

    // ---- Drop target (rotation list) ----

    void RotationList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(WindowFormat) ? DragDropEffects.Copy
            : e.Data.GetDataPresent(EntryFormat) ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
        if (e.Effects == DragDropEffects.None) { DropLine.Visibility = Visibility.Collapsed; return; }
        DropIndex(e, out double y);
        ShowDropLine(y);
        AutoScroll(e);
    }

    void RotationList_DragLeave(object sender, DragEventArgs e) => DropLine.Visibility = Visibility.Collapsed;

    void RotationList_Drop(object sender, DragEventArgs e)
    {
        DropLine.Visibility = Visibility.Collapsed;
        int index = DropIndex(e, out _);
        if (e.Data.GetData(WindowFormat) is WindowInfo window) Vm.AddWindow(window, index);
        else if (e.Data.GetData(EntryFormat) is RotationEntry entry) Vm.MoveTo(entry, index);
        e.Handled = true;
    }

    /// <summary>Insert position under the mouse: before the item whose upper half is hovered.</summary>
    int DropIndex(DragEventArgs e, out double lineY)
    {
        var list = RotationList;
        lineY = 0;
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement item || !item.IsVisible) continue;
            var top = item.TranslatePoint(new Point(0, 0), DropCanvas).Y;
            lineY = top + item.ActualHeight;
            if (e.GetPosition(item).Y < item.ActualHeight / 2) { lineY = top - 1; return i; }
        }
        return list.Items.Count;
    }

    void ShowDropLine(double y)
    {
        Canvas.SetTop(DropLine, Math.Max(0, y));
        Canvas.SetLeft(DropLine, 4);
        DropLine.Width = Math.Max(0, DropCanvas.ActualWidth - 8);
        DropLine.Visibility = Visibility.Visible;
    }

    void AutoScroll(DragEventArgs e)
    {
        if (FindChild<ScrollViewer>(RotationList) is not { } scroll) return;
        double y = e.GetPosition(RotationList).Y;
        if (y < 24) scroll.LineUp();
        else if (y > RotationList.ActualHeight - 24) scroll.LineDown();
    }

    // ---- Keyboard & mouse shortcuts ----

    void LibraryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource as DependencyObject)?.DataContext is WindowInfo window) Vm.AddWindow(window);
    }

    void LibraryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm.SelectedWindow is { } window) { Vm.AddWindow(window); e.Handled = true; }
    }

    void RotationList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || Vm.SelectedEntry is not { } entry) return;
        if (e.Key == Key.Delete) { Vm.RemoveCommand.Execute(entry); e.Handled = true; }
        else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt) { Vm.MoveUpCommand.Execute(entry); e.Handled = true; }
        else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt) { Vm.MoveDownCommand.Execute(entry); e.Handled = true; }
    }

    void DurationBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        box.SelectAll();
        if (ItemAt(box) is { } item) item.IsSelected = true;
    }

    void DurationBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ((TextBox)sender).GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    // ---- Save-as popup ----

    void SaveAsPopup_Opened(object sender, EventArgs e) => Dispatcher.BeginInvoke(() => { NewProfileNameBox.Focus(); Keyboard.Focus(NewProfileNameBox); });

    void SaveAsPopup_Closed(object sender, EventArgs e) => SaveAsToggle.IsChecked = false;

    void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAsToggle.IsChecked = false;

    void NewProfileNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm.SaveProfileAsCommand.CanExecute(null)) { Vm.SaveProfileAsCommand.Execute(null); SaveAsToggle.IsChecked = false; }
        else if (e.Key == Key.Escape) SaveAsToggle.IsChecked = false;
    }

    // ---- Visual tree helpers ----

    static ListBoxItem? ItemAt(DependencyObject? source)
    {
        for (var d = source; d != null; d = ParentOf(d))
            if (d is ListBoxItem item) return item;
        return null;
    }

    // Run and other content elements are not visuals.
    static DependencyObject? ParentOf(DependencyObject d) => d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    /// <summary>Clicks on inputs and buttons inside a row must not start a drag.</summary>
    static bool IsInteractive(DependencyObject? source)
    {
        for (var d = source; d != null && d is not ListBoxItem; d = ParentOf(d))
            if (d is TextBoxBase or ButtonBase or ScrollBar) return true;
        return false;
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? FindChild<T>(child)) is { } found) return found;
        }
        return null;
    }
}

public sealed class PlusOneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int i ? i + 1 : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value == null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
