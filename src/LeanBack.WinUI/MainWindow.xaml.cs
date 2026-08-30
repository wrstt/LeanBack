using System.Runtime.InteropServices;
using LeanBack.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace LeanBack;

public sealed partial class MainWindow : Window
{
    // Tall enough that Advanced — skip rows, patterns, format, history — fits without scrolling.
    private const int LogicalWidth = 680;
    private const int LogicalHeight = 940;

    public MainViewModel Vm { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        Title = "LeanBack — Smart project backup";

        // Native caption buttons stay live (Snap Layouts, Aero Snap, window shadow);
        // we only claim the empty strip to the left of them as draggable.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Vm.Hwnd = hwnd;

        SizeAndCentre(hwnd);

        Activated += OnFirstActivated;
    }

    private bool _initialised;
    private bool _syncingTabs;

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_initialised) return;
        _initialised = true;

        await Vm.InitialiseAsync();

        // SelectorBar tracks each item's own IsSelected, so set both explicitly — leaving the
        // other one selected makes the bar resolve back to it and overwrite the saved mode.
        _syncingTabs = true;
        SimpleTab.IsSelected = !Vm.IsAdvanced;
        AdvancedTab.IsSelected = Vm.IsAdvanced;
        _syncingTabs = false;
    }

    /// <summary>
    /// AppWindow.Resize takes physical pixels, so a fixed value shrinks the window on a
    /// scaled display (620 physical is only 413 logical at 150%). Scale by the window's DPI.
    /// </summary>
    private void SizeAndCentre(IntPtr hwnd)
    {
        double scale = GetDpiForWindow(hwnd) / 96.0;
        int w = (int)Math.Round(LogicalWidth * scale);
        int h = (int)Math.Round(LogicalHeight * scale);

        // Never exceed the work area on a smaller or more heavily scaled display.
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        w = Math.Min(w, (int)(area.WorkArea.Width * 0.95));
        h = Math.Min(h, (int)(area.WorkArea.Height * 0.95));

        AppWindow.Resize(new SizeInt32(w, h));
        AppWindow.Move(new PointInt32(
            area.WorkArea.X + (area.WorkArea.Width - w) / 2,
            area.WorkArea.Y + (area.WorkArea.Height - h) / 2));
    }

    // ------------------------------------------------------------- handlers

    private void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingTabs) return;
        Vm.Advanced = ReferenceEquals(sender.SelectedItem, AdvancedTab);
    }

    private void OnCustomKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        if (Vm.AddCustomCommand.CanExecute(null)) Vm.AddCustomCommand.Execute(null);
        e.Handled = true;
    }

    private void OnRemoveCustom(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string pattern })
            Vm.RemoveCustomCommand.Execute(pattern);
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HistoryItem h })
            Vm.RestoreCommand.Execute(h);
    }

    // ------------------------------------------------------------- x:Bind helpers

    public Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility VisAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
