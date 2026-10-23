using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class SheetBindingChecks
{
    public static void Verify()
    {
        var tests = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!; var root = Path.Combine(tests, "wpf_sheet_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        var preceding = new SqliteJournalStore(BindingFixtures.Get("nutrition")).ReadSnapshot();
        var backups = new BackupService(store, Path.Combine(root, "recovery")); backups.Restore(preceding); var journal = new JournalService(store);
        journal.SaveSettings(JournalSettings.Default with { NewFoodMode = "never" });
        var vm = new MainViewModel(journal, backups, root); var window = new MainWindow(vm); window.Show(); Pump();
        var tabs = (TabControl)window.FindName("MainTabs"); Assert.AreEqual("设置与数据", ((TabItem)tabs.Items[tabs.Items.Count - 1]).Header); Assert.IsFalse(vm.SheetMode);
        vm.TabIndex = 1; var query = (TextBox)window.FindName("LibrarySearchBox");
        query.Text = "明确的不存在词"; Pump(); Assert.IsEmpty(vm.LibraryFoods); query.ApplyTemplate();
        var clear = (Button)query.Template.FindName("ClearTextButton", query); Assert.IsTrue(clear.IsVisible && !clear.Focusable); Invoke(clear); Assert.AreEqual("", vm.LibraryQuery); Assert.IsTrue(vm.LibraryFoods.Count > 0);
        query.Undo(); Pump(); Assert.AreEqual("明确的不存在词", vm.LibraryQuery); query.Redo(); Pump(); Assert.AreEqual("", vm.LibraryQuery);
        query.Undo(); Pump(); Invoke(clear); Assert.AreEqual(Visibility.Collapsed, clear.Visibility);
        query.Text = "禁用保护"; query.IsEnabled = false; Pump(); Assert.AreEqual(Visibility.Collapsed, clear.Visibility); query.IsEnabled = true; Invoke(clear);
        vm.TabIndex = 6; Pump();
        var readonlyBox = Descendants<TextBox>(window).First(x => x.IsReadOnly); readonlyBox.ApplyTemplate();
        Assert.AreEqual(Visibility.Collapsed, ((Button)readonlyBox.Template.FindName("ClearTextButton", readonlyBox)).Visibility);
        vm.TabIndex = 6; vm.SettingsDailyPage = 1; vm.SaveSettingsCommand.Execute(null); Pump(); Assert.IsTrue(vm.SheetMode); Assert.AreEqual("sheet", journal.Settings().DailyRecordPage);
        vm.TabIndex = 0; Pump(); var sheet = (SheetJournalView)window.FindName("SheetDailyPage"); Assert.IsFalse(((Grid)window.FindName("ClassicDailyPage")).IsVisible);
        var today = DateOnly.FromDateTime(DateTime.Today); var day = vm.SheetDays.Single(x => x.Date == today); var lunch = day.Meals.Single(x => x.Meal.Name == "午餐");
        var count = store.ReadSnapshot().Entries.Length; lunch.AddCommand.Execute(null); Pump(); var row = lunch.Rows.Last(); Assert.IsTrue(row.HasDraft && row.WeightMode); Assert.AreEqual(count, store.ReadSnapshot().Entries.Length);
        var input = RowControl<FoodNameInput>(row, "SheetFoodInput", 1); var name = (TextBox)input.FindName("NameInput"); name.Focus(); name.Text = "明确WPF营养标签"; Pump();
        var popup = (Popup)input.FindName("SuggestionsPopup"); var candidates = (ListBox)input.FindName("SuggestionsList"); Assert.IsTrue(popup.IsOpen); Assert.IsTrue(candidates.Items.Count > 0);
        PressKey(name, Key.Down); PressKey(candidates, Key.Enter); Assert.IsFalse(popup.IsOpen); Assert.AreEqual("明确WPF营养标签食物", row.Name); Assert.AreEqual("kJ", row.UnitEnergyType);
        RowControl<TextBox>(row, "SheetWeightBox", 3).Text = "150"; Pump(); Assert.IsTrue(row.EnergyPreview.Contains("150.0")); Assert.IsTrue(row.ProteinPreview.Contains("60.00"));
        Invoke(RowControl<Button>(row, "SaveSheetRowButton", 0)); Assert.AreEqual(count + 1, store.ReadSnapshot().Entries.Length); Assert.IsFalse(row.HasDraft);
        var saved = store.FindEntry(row.Id)!; Assert.AreEqual(6000L, saved.ProteinGramsHundredths); Assert.AreEqual(41840L, saved.KilojoulesHundredthsSnapshot);
        row.SaveCommand.Execute(null); Assert.AreEqual(count + 1, store.ReadSnapshot().Entries.Length);
        lunch.AddCommand.Execute(null); var blank = lunch.Rows.Last(); vm.SettingsDailyPage = 0; vm.SaveSettingsCommand.Execute(null); Assert.IsTrue(vm.SheetMode); Assert.IsTrue(vm.Status.Contains("先保存或取消")); vm.ReloadSettingsCommand.Execute(null);
        var appliedCount = vm.SheetDays.Count; vm.SheetFrom = DateTime.Today.AddDays(-2); vm.QuerySheetCommand.Execute(null); Assert.AreEqual(appliedCount, vm.SheetDays.Count); Assert.IsTrue(blank.HasDraft);
        vm.RestoreCommand.ExecuteAsync(null).GetAwaiter().GetResult(); Assert.IsTrue(vm.Status.Contains("请先保存或取消")); Assert.AreEqual(1, ConfirmationChecks.Respond(() => Assert.IsFalse(vm.CanClose()), false));
        blank.CancelCommand.Execute(null); Assert.IsFalse(vm.HasSheetDrafts);
        row.Note = "修改但取消"; Assert.AreEqual(1, ConfirmationChecks.Respond(() => row.CancelCommand.Execute(null), false)); Assert.AreEqual("修改但取消", row.Note);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => row.CancelCommand.Execute(null), true)); Assert.AreEqual(saved.Note, row.Note);
        var food = journal.Foods().Single(x => x.Id == saved.FoodId); journal.SaveFood(food with { ProteinPer100gHundredths = 5000 });
        row.Weight = "200"; row.SaveCommand.Execute(null); Assert.AreEqual(8000L, store.FindEntry(row.Id)!.ProteinGramsHundredths); Assert.AreEqual(6000L, saved.ProteinGramsHundredths);
        row.Date = DateTime.Today.AddDays(-1); row.Meal = row.Meals.Single(x => x.Name == "晚餐"); row.SaveCommand.Execute(null); Assert.AreEqual(today.AddDays(-1), store.FindEntry(row.Id)!.EntryDate);
        Assert.IsTrue(vm.SheetDays.Single(x => x.Date == today.AddDays(-1)).Meals.Single(x => x.Meal.Name == "晚餐").Rows.Contains(row));
        var originalName = row.Name; row.Name = "手动名称已修改"; Assert.IsNull(row.CreateEntry().FoodId); row.Revert(); Assert.AreEqual(originalName, row.Name);
        row.DirectEnergy = "99"; var direct = RowControl<TextBox>(row, "SheetDirectEnergyBox", 6); direct.Focus(); Pump(); Assert.IsFalse(row.WeightMode); row.DirectProtein = "0"; row.SaveCommand.Execute(null);
        Assert.AreEqual(990L, store.FindEntry(row.Id)!.EnergyKcalTenths); Assert.AreEqual(0L, store.FindEntry(row.Id)!.ProteinGramsHundredths);
        var recentDay = vm.SheetDays.Single(x => x.Date == today); lunch = recentDay.Meals.Single(x => x.Meal.Name == "午餐");
        var manual = New(lunch, "明确表格单次舍入"); manual.UnitEnergyType = "kJ"; manual.UnitEnergy = "1.00"; manual.UnitProtein = "0.01"; manual.Weight = "10000"; manual.SaveCommand.Execute(null);
        Assert.AreEqual(239L, store.FindEntry(manual.Id)!.EnergyKcalTenths); Assert.AreEqual(100L, store.FindEntry(manual.Id)!.ProteinGramsHundredths);
        var converter = new WeightConversionViewModel(journal, null, 24, null, 100) { Gross = "10001" }; converter.Deductions[0].Name = "皮"; converter.Deductions[0].Weight = "1"; Assert.IsTrue(converter.Preview.Contains("23.9"));
        var pending = New(lunch, "明确表格事务输入"); pending.UnitEnergy = "120"; pending.Weight = "100";
        using(var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False")) { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER fail_sheet BEFORE INSERT ON entries BEGIN SELECT RAISE(ABORT,'明确表格事务失败'); END;"; command.ExecuteNonQuery(); }
        var beforeFailure = store.ReadSnapshot(); pending.SaveCommand.Execute(null); Assert.IsTrue(pending.HasDraft); Assert.IsTrue(pending.Message.Contains("未完成")); Assert.AreEqual(beforeFailure.Entries.Length, store.ReadSnapshot().Entries.Length);
        using(var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False")) { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TRIGGER fail_sheet"; command.ExecuteNonQuery(); }
        pending.SaveCommand.Execute(null); Assert.IsNotNull(store.FindEntry(pending.Id));
        vm.TabIndex = 6; vm.SettingsFoodMode = 0; vm.SaveSettingsCommand.Execute(null); vm.TabIndex = 0;
        var asked = New(lunch, "明确表格新资料询问"); asked.UnitEnergy = "140"; asked.UnitProtein = "2"; asked.Weight = "100";
        Assert.AreEqual(1, ConfirmationChecks.RespondChoice(() => asked.SaveCommand.Execute(null), 2)); Assert.IsNull(store.FindEntry(asked.Id));
        Assert.AreEqual(1, ConfirmationChecks.RespondChoice(() => asked.SaveCommand.Execute(null), 6)); Assert.IsNotNull(store.FindEntry(asked.Id)!.FoodId); Assert.AreEqual(200L, journal.Foods(asked.Name).Single().ProteinPer100gHundredths);
        var decline = New(lunch, "明确表格新资料拒绝"); decline.UnitEnergy = "100"; decline.Weight = "10"; Assert.AreEqual(1, ConfirmationChecks.RespondChoice(() => decline.SaveCommand.Execute(null), 7)); Assert.IsNull(store.FindEntry(decline.Id)!.FoodId);
        var zero = New(lunch, "明确表格零摄入"); zero.EnterDirect(); zero.DirectEnergy = "0"; zero.DirectProtein = ""; zero.SaveCommand.Execute(null); Assert.AreEqual(0L, store.FindEntry(zero.Id)!.EnergyKcalTenths); Assert.IsNull(store.FindEntry(zero.Id)!.ProteinGramsHundredths);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => pending.DeleteCommand.Execute(null), false)); Assert.IsNotNull(store.FindEntry(pending.Id));
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => pending.DeleteCommand.Execute(null), true)); Assert.IsNull(store.FindEntry(pending.Id)); Assert.IsFalse(vm.HasSheetDrafts);
        vm.TodaySheetCommand.Execute(null); vm.EarlierSheetCommand.Execute(null); Assert.AreEqual(31, vm.SheetDays.Count);
        var range = vm.SheetRangeText; vm.SheetTo = DateTime.Today.AddDays(1); vm.QuerySheetCommand.Execute(null); Assert.AreEqual(range, vm.SheetRangeText); Assert.IsTrue(vm.Status.Contains("晚于今天")); vm.TodaySheetCommand.Execute(null);
        var layouts = new List<object>(); var page = (Grid)window.Content;
        foreach(var scale in new[] {1.0,1.25,1.5,2.0})
        {
            window.Width = 920 * scale; window.Height = 600 * scale; page.LayoutTransform = new ScaleTransform(scale, scale); Pump();
            foreach(var controlName in new[] {"QuerySheetButton","TodaySheetButton","SheetTemplatesButton","EarlierSheetButton"}) Check((FrameworkElement)sheet.FindName(controlName), scale);
            foreach(var item in new[] {("SaveSheetRowButton",0),("CancelSheetRowButton",0),("SheetFoodInput",1),("SheetWeightBox",3),("SheetDirectEnergyBox",6)}) Check(RowControl<FrameworkElement>(manual, item.Item1, item.Item2), scale);
            var grid = GridFor(manual); grid.ScrollIntoView(manual); grid.SelectedItem = manual; Pump(); var expander = RowControl<Expander>(manual, "SheetDetailsExpander", 0);
            Check(expander, scale); expander.IsExpanded = true; Pump();
            foreach(var controlName in new[] {"SheetWeightConversionButton","DeleteSheetRowButton"}) Check(RowControl<Button>(manual, controlName, 0), scale);
            expander.IsExpanded = false;
        }
        page.LayoutTransform = Transform.Identity; window.Width = 1240; window.Height = 820; Pump();
        var dayScroller = Descendants<ScrollViewer>((ListBox)sheet.FindName("SheetDaysList")).First();
        dayScroller.ScrollToTop(); Pump();
        var firstGrid = Descendants<DataGrid>(sheet).First();
        var rowScroller = Descendants<ScrollViewer>(firstGrid).First(); rowScroller.ScrollToBottom(); Pump();
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        firstGrid.RaiseEvent(wheel); Pump(); Assert.IsTrue(wheel.Handled); Assert.IsGreaterThan(0.0, dayScroller.VerticalOffset);
        vm.TabIndex = 6; vm.SettingsDailyPage = 0; vm.SaveSettingsCommand.Execute(null); Assert.IsFalse(vm.SheetMode); vm.SettingsDailyPage = 1; vm.SaveSettingsCommand.Execute(null); Assert.IsTrue(vm.SheetMode);
        backups.Export(Path.Combine(root, "sheet-complete.calorie-backup.json")); Assert.IsFalse(vm.HasSheetDrafts); Assert.IsTrue(vm.CanClose()); window.Close();
        var restarted = new MainViewModel(new JournalService(new SqliteJournalStore(store.DatabasePath)), backups, root); Assert.IsTrue(restarted.SheetMode); Assert.AreEqual("sheet", store.ReadSnapshot().Settings!.DailyRecordPage);
        JsonFiles.WriteNew(Path.Combine(root, "sheet-checks.json"), new { Success = true, ClearBindingSearchAndUndo = true, ClearReadonlyAndEmpty = true, SettingsLastAndRestart = true, RealInlineCrudAndMove = true, NameSuggestionKeyboard = true, BlankRowsNeverSaved = true, DraftProtectionAndStableRefresh = true, OriginalSnapshotsRetained = true, DirectFocusSwitch = true, KilojouleSingleRoundingAndProtein = true, AskYesNoCancel = true, RealTransactionFailureRetainsDraft = true, InvalidRangeDoesNotPolluteRefresh = true, AllNineCollectionsAndSettingsBackup = true, LayoutChecks = layouts });
        SheetEntryViewModel New(SheetMealViewModel group, string nameValue) { group.AddCommand.Execute(null); var result = group.Rows.Last(); result.Name = nameValue; return result; }
        DataGrid GridFor(SheetEntryViewModel value)
        {
            var date = vm.SheetDays.Single(x => x.Meals.Any(g => g.Rows.Contains(value))); ((ListBox)sheet.FindName("SheetDaysList")).ScrollIntoView(date); Pump();
            return Descendants<DataGrid>(sheet).Single(x => x.Items.Contains(value));
        }
        T RowControl<T>(SheetEntryViewModel value, string nameValue, int column) where T : FrameworkElement
        {
            var grid = GridFor(value); grid.SelectedItem = value; grid.ScrollIntoView(value, grid.Columns[column]); Pump();
            return Descendants<T>(grid).First(x => x.Name == nameValue && ReferenceEquals(x.DataContext, value));
        }
        void Check(FrameworkElement control, double scale) { control.BringIntoView(); Pump(); var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize)); Assert.IsTrue(control.IsVisible && bounds.Width > 0 && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight + 1 && bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 1, $"{control.Name}在{scale}下不可达：{bounds}"); layouts.Add(new {Scale=scale,Control=control.Name,Bounds=bounds.ToString()}); }
    }
    private static void Invoke(Button button) { ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); Pump(); }
    private static void PressKey(UIElement target, Key key) { target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Pump(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++) { var child=VisualTreeHelper.GetChild(root,index); if(child is T value) yield return value; foreach(var nested in Descendants<T>(child)) yield return nested; } }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
}
