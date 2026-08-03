using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceScreen.App.Models;

namespace VoiceScreen.App;

public partial class OverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;
    private readonly int _historyCapacity;
    private bool _followLatest = true;

    public bool IsInteractive { get; private set; }

    public ObservableCollection<SubtitleLine> Lines { get; } = new();

    public OverlayWindow(int maxLines, double left, double top, double width, double height)
    {
        InitializeComponent();
        _historyCapacity = Math.Max(200, maxLines * 20);
        Left = left;
        Top = top;
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        DataContext = this;
        SourceInitialized += (_, _) => EnableClickThrough();
    }

    public void AddLine(string kind, string text)
    {
        Dispatcher.Invoke(() =>
        {
            Lines.Add(new SubtitleLine { Kind = kind, Text = text });
            while (Lines.Count > _historyCapacity) Lines.RemoveAt(0);
            if (_followLatest) SubtitleList.ScrollIntoView(Lines.Last());
        });
    }

    public void ScrollPage(bool down)
    {
        Dispatcher.Invoke(() =>
        {
            var viewer = FindVisualChild<ScrollViewer>(SubtitleList);
            if (viewer is null) return;
            if (down)
            {
                viewer.PageDown();
                Dispatcher.BeginInvoke(() =>
                    _followLatest = viewer.VerticalOffset >= viewer.ScrollableHeight - 1,
                    DispatcherPriority.Background);
            }
            else
            {
                _followLatest = false;
                viewer.PageUp();
            }
        });
    }

    public void SetInteractive(bool interactive)
    {
        Dispatcher.Invoke(() =>
        {
            IsInteractive = interactive;
            EditHint.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
            ResizeHandle.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
            ApplyWindowStyle();
            if (interactive) Activate();
        });
    }

    public void SetStatus(string text, bool ok = false, bool error = false)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            StatusDot.Fill = error ? Brushes.IndianRed : ok ? Brushes.MediumSpringGreen : Brushes.Gold;
        });
    }

    private void EnableClickThrough() => ApplyWindowStyle();

    private void ApplyWindowStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExstyle).ToInt64();
        style |= WsExToolwindow;
        if (IsInteractive)
            style &= ~(WsExTransparent | WsExNoactivate);
        else
            style |= WsExTransparent | WsExNoactivate;
        SetWindowLongPtr(handle, GwlExstyle, new IntPtr(style));
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!IsInteractive) return;
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) return result;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
