using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
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
        var tabsPeer = UIElementAutomationPeer.CreatePeerForElement((TabControl)window.FindName("MainTabs"))!;
        Assert.IsTrue(AutomationDescendants(tabsPeer).Any(x => x.GetAutomationId() == "SaveEntryButton"),
            "页面控件须保留在辅助技术可访问的导航内容区域中。");
        var originalDate = vm.SelectedDate;
        vm.SelectedDate = DateTime.Today.AddDays(-14); Pump();
        Assert.IsTrue(((StackPanel)window.FindName("EntriesEmptyState")).IsVisible);
        vm.SelectedDate = originalDate; Pump();
        Assert.IsFalse(((StackPanel)window.FindName("EntriesEmptyState")).IsVisible);
        vm.TabIndex = 1; vm.LibraryQuery = "明确用于界面验收的不匹配搜索词"; Pump();
        Assert.AreEqual(0, vm.LibraryFoods.Count);
        Assert.IsTrue(((StackPanel)window.FindName("FoodsEmptyState")).IsVisible);
        vm.LibraryQuery = ""; Pump();
        Assert.IsFalse(((StackPanel)window.FindName("FoodsEmptyState")).IsVisible);
        vm.TabIndex = 2; Pump();
        var history = (DataGrid)window.FindName("HistoryGrid");
        foreach (var (summary, expected) in new[]
        {
            (vm.HistoryRows.First(x => x.Count == 0), "—"),
            (vm.HistoryRows.Single(x => x.Date == DateOnly.FromDateTime(originalDate)), "0.0")
        })
        {
            history.ScrollIntoView(summary); Pump();
            var row = (DataGridRow)history.ItemContainerGenerator.ContainerFromItem(summary);
            Assert.IsTrue(Descendants<TextBlock>(row).Any(x => x.Text == expected),
                "无记录日期显示横线，真实零热量仍须显示0.0。");
        }
        vm.TabIndex = 0; Pump();
        var entryHeading = Descendants<TextBlock>(window).Single(x => x.Text == vm.EntryHeading);
        Assert.IsTrue(entryHeading.Foreground is SolidColorBrush { Color.A: > 0 } headingBrush && headingBrush.Color != Colors.White,
            "选中导航的白色标题不能传播到页面正文。");
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
        var directUnit = (ComboBox)window.FindName("DirectEnergyUnitBox");
        Assert.IsTrue(directUnit.IsKeyboardFocusWithin); Assert.IsFalse(vm.WeightMode);
        Assert.IsTrue(directUnit.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))); Pump();
        var protein = (TextBox)window.FindName("DirectProteinBox"); Assert.IsTrue(protein.IsKeyboardFocusWithin);
        Assert.IsTrue(protein.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))); Pump();
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
                (0, new[] { "SaveEntryButton", "NewEntryButton", "CopyEntryButton", "DeleteEntryButton", "WeightConversionButton" }),
                (1, new[] { "SaveFoodButton", "DeleteFoodButton", "UseFoodButton", "OpenFoodLookupButton" }),
                (2, new[] { "HistoryQueryButton", "OpenHistoryDayButton" }),
                (6, new[] { "ExportBackupButton", "RestoreBackupButton", "ExportDatabaseButton", "OpenImportButton", "SaveMealButton", "NewMealButton", "SaveSettingsButton", "ReloadSettingsButton" })
            })
            {
                vm.TabIndex = tab; Pump(); window.UpdateLayout();
                if (tab == 0)
                {
                    var scroll = (ScrollViewer)window.FindName("EntryFormScroll");
                    var save = (Button)window.FindName("SaveEntryButton");
                    scroll.ScrollToTop(); Pump();
                    var top = save.TransformToAncestor(window).TransformBounds(new Rect(save.RenderSize));
                    scroll.ScrollToBottom(); Pump();
                    var bottom = save.TransformToAncestor(window).TransformBounds(new Rect(save.RenderSize));
                    Assert.AreEqual(top, bottom, "表单滚动时保存按钮应保持在固定区域。");
                }
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
            WpfFocusTraversal = true, EmptyStates = true, ZeroAndUnrecordedDisplay = true,
            FixedSaveFooter = true, HeadingContrast = true, AccessibleTabContent = true,
            LayoutScaleChecks = checks, SystemDpiSwitchTested = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static IEnumerable<AutomationPeer> AutomationDescendants(AutomationPeer parent)
    {
        foreach (var child in parent.GetChildren() ?? [])
        {
            yield return child;
            foreach (var descendant in AutomationDescendants(child)) yield return descendant;
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
