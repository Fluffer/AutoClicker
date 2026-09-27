using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace AutoClicker.WinUI;

/// <summary>
/// M1 skeleton window. ExtendsContentIntoTitleBar stays at its default (false) and the whole
/// view is a single centred TextBlock — the M2 port replaces the Grid's contents with the
/// ListView / ToolStrip / StatusStrip equivalents.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 600;
    private const int DefaultHeight = 400;

    public MainWindow()
    {
        InitializeComponent();
        ResizeToDefault();
    }

    /// <summary>
    /// AppWindow sizes in physical pixels, so on this 4K@150% box the window is 600x400
    /// physical (~400x267 effective) — matching the WinForms app's "fixed-size form" habit.
    /// </summary>
    private void ResizeToDefault()
    {
        AppWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));

        // Belt-and-braces for the unpackaged path: make sure the HWND really is non-null
        // before anything in M2 starts calling into it (presenters, file pickers, DPI).
        _ = WindowNative.GetWindowHandle(this);
    }
}
