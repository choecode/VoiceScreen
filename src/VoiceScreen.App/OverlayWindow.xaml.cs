using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VoiceScreen.App.Models;

namespace VoiceScreen.App;

public partial class OverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;
    private readonly int _maxLines;

    public ObservableCollection<SubtitleLine> Lines { get; } = new();

    public OverlayWindow(int maxLines, double left, double top)
    {
        InitializeComponent();
        _maxLines = Math.Max(3, maxLines);
        Left = left;
        Top = top;
        DataContext = this;
        SourceInitialized += (_, _) => EnableClickThrough();
    }

    public void AddLine(string kind, string text)
    {
        Dispatcher.Invoke(() =>
        {
            Lines.Add(new SubtitleLine { Kind = kind, Text = text });
            while (Lines.Count > _maxLines) Lines.RemoveAt(0);
            SubtitleList.ScrollIntoView(Lines.Last());
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

    private void EnableClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExstyle).ToInt64();
        SetWindowLongPtr(handle, GwlExstyle, new IntPtr(style | WsExTransparent | WsExNoactivate | WsExToolwindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
