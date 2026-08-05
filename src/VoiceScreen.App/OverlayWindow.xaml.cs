using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    private int _historyCursor = -1;

    public bool IsInteractive { get; private set; }

    public ObservableCollection<SubtitleLine> Lines { get; } = new();

    public OverlayWindow(int maxLines, double left, double top, double width, double height, double fontSize)
    {
        InitializeComponent();
        _historyCapacity = Math.Max(200, maxLines * 20);
        Left = left;
        Top = top;
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        SubtitleList.FontSize = Math.Clamp(fontSize, 14, 42);
        DataContext = this;
        SourceInitialized += (_, _) => EnableClickThrough();
    }

    public void AddLine(string kind, string text)
    {
        Dispatcher.Invoke(() =>
        {
            // Incoming final subtitles replace the realtime preview. Keep both changes in
            // one dispatcher turn so WPF cannot render the same utterance in preview and
            // history at the same time between two separate UI updates.
            if (string.Equals(kind, "remote", StringComparison.OrdinalIgnoreCase))
            {
                PreviewText.Text = string.Empty;
                PreviewBorder.Visibility = Visibility.Collapsed;
            }

            Lines.Add(new SubtitleLine { Kind = kind, Text = text });
            while (Lines.Count > _historyCapacity)
            {
                Lines.RemoveAt(0);
                if (_historyCursor > 0) _historyCursor--;
            }
            if (_followLatest)
            {
                _historyCursor = Lines.Count - 1;
                SubtitleList.ScrollIntoView(Lines.Last());
            }
            UpdateHistoryIndicator();
        });
    }

    public void ScrollPage(bool down)
    {
        Dispatcher.Invoke(() =>
        {
            if (Lines.Count == 0)
            {
                UpdateHistoryIndicator();
                return;
            }
            var pageSize = Math.Max(1, (int)Math.Floor(SubtitleList.ActualHeight / (SubtitleList.FontSize * 3)));
            var current = _followLatest || _historyCursor < 0 ? Lines.Count - 1 : _historyCursor;
            _historyCursor = down
                ? Math.Min(Lines.Count - 1, current + pageSize)
                : Math.Max(0, current - pageSize);
            _followLatest = _historyCursor >= Lines.Count - 1;
            SubtitleList.ScrollIntoView(Lines[_historyCursor]);
            UpdateHistoryIndicator();
        });
    }

    public void SetFontSize(double fontSize)
    {
        Dispatcher.Invoke(() =>
        {
            SubtitleList.FontSize = Math.Clamp(fontSize, 14, 42);
            PreviewText.FontSize = Math.Clamp(fontSize, 14, 42);
            if (_followLatest && Lines.Count > 0) SubtitleList.ScrollIntoView(Lines.Last());
        });
    }

    public void SetPreview(string? text)
    {
        Dispatcher.Invoke(() =>
        {
            PreviewText.Text = text ?? string.Empty;
            PreviewText.FontSize = SubtitleList.FontSize;
            PreviewBorder.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void UpdateHistoryIndicator()
    {
        if (Lines.Count == 0)
            HistoryIndicator.Text = "最新 · 0 条";
        else if (_followLatest)
            HistoryIndicator.Text = $"最新 · {Lines.Count} 条";
        else
            HistoryIndicator.Text = $"历史 {_historyCursor + 1}/{Lines.Count} · PgUp/PgDn";
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

}
