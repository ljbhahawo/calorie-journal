using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CalorieJournal.Desktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class WindowLayoutChecks
{
    public static void Verify(MainWindow window, MainViewModel vm, string reportPath)
    {
        window.Show(); window.Activate(); Pump();
        var actualDpi = VisualTreeHelper.GetDpi(window);
        var name = (TextBox)window.FindName("EntryNameBox");
        name.Focus(); Pump();
        Assert.IsTrue(name.IsKeyboardFocusWithin);
        Assert.IsTrue(name.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
        Pump();
        Assert.IsTrue(((TextBox)window.FindName("EntryPreparationBox")).IsKeyboardFocusWithin);
        var kcal = (TextBox)window.FindName("DirectKcalBox");
        kcal.Focus(); Pump();
        Assert.IsTrue(kcal.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
        Pump();
        var note = (TextBox)window.FindName("EntryNoteBox");
        Assert.IsTrue(note.IsKeyboardFocusWithin);
        Assert.IsTrue(note.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
        Pump();
        Assert.IsTrue(((Button)window.FindName("SaveEntryButton")).IsKeyboardFocusWithin);

        var root = (Grid)window.Content;
        var checks = new List<object>();
        foreach (var factor in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            // 在真实WPF消息循环中缩放布局；不改变系统DPI，也不冒充多显示器切换测试。
            window.Width = 920 * factor; window.Height = 600 * factor;
            root.LayoutTransform = new ScaleTransform(factor, factor);
            foreach (var (tab, names) in new[]
            {
                (0, new[] { "SaveEntryButton", "NewEntryButton", "CopyEntryButton", "DeleteEntryButton" }),
                (1, new[] { "SaveFoodButton", "DeleteFoodButton", "UseFoodButton" }),
                (2, new[] { "HistoryQueryButton", "OpenHistoryDayButton" }),
                (3, new[] { "ExportBackupButton", "RestoreBackupButton", "SaveMealButton", "NewMealButton" })
            })
            {
                vm.TabIndex = tab; Pump(); window.UpdateLayout();
                foreach (var controlName in names)
                {
                    var button = (Button)window.FindName(controlName);
                    button.BringIntoView(); Pump(); window.UpdateLayout();
                    var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                    Assert.IsTrue(button.IsVisible && bounds.Width > 0 && bounds.Height > 0, controlName);
                    Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight + 1,
                        $"缩放{factor}时{controlName}不可完整访问：{bounds}，窗口{window.ActualWidth}×{window.ActualHeight}");
                    checks.Add(new { Scale = factor, Tab = tab, Control = controlName, VisibleBounds = bounds.ToString() });
                }
            }
        }
        root.LayoutTransform = Transform.Identity; window.Width = 1240; window.Height = 820;
        vm.TabIndex = 0; Pump(); window.Hide();
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            ActualMonitorDpiX = actualDpi.PixelsPerInchX, ActualMonitorDpiY = actualDpi.PixelsPerInchY,
            WpfFocusTraversal = true, LayoutScaleChecks = checks, SystemDpiSwitchTested = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
