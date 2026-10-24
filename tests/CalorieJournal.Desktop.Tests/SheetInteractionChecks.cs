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

internal static class SheetInteractionChecks
{
    public static void Verify()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "wpf_sheet_interaction_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        var backups = new BackupService(store, Path.Combine(root, "recovery")); backups.Restore(new SqliteJournalStore(BindingFixtures.Get("sheet")).ReadSnapshot());
        var journal = new JournalService(store); journal.SaveSettings(journal.Settings() with { NewFoodMode = "never" });
        var vm = new MainViewModel(journal, backups, root); var window = new MainWindow(vm); window.Show(); Pump(); vm.TodaySheetCommand.Execute(null); Pump();
        var sheet = (SheetJournalView)window.FindName("SheetDailyPage");
        var lunch = vm.SheetDays.Single().Meals.Single(x => x.Meal.Name == "午餐");
        var initial = store.ReadSnapshot().Entries.Length;
        var row = New(); var name = Name(row); name.Text = "明确回车自动保存"; row.EnterDirect(); row.DirectEnergy = "10"; Press(name, Key.Enter);
        Assert.IsFalse(row.HasDraft); Assert.AreEqual(initial + 1, store.ReadSnapshot().Entries.Length); Press(name, Key.Enter); Assert.AreEqual(initial + 1, store.ReadSnapshot().Entries.Length);
        name.Text = "明确离开行保存"; Assert.IsFalse(Outside()); Assert.AreEqual(name.Text, store.FindEntry(row.Id)!.FoodNameSnapshot);
        name.Text = "明确点击说明文字保存"; Assert.IsFalse(Outside(true)); Assert.AreEqual(name.Text, store.FindEntry(row.Id)!.FoodNameSnapshot);
        var blank = New(); Assert.IsFalse(Outside()); Assert.IsNull(store.FindEntry(blank.Id)); blank.CancelCommand.Execute(null);
        var invalid = New(); Name(invalid).Text = "明确未完整记录"; Assert.IsTrue(Outside()); Assert.IsNull(store.FindEntry(invalid.Id)); Assert.IsTrue(invalid.Message.Contains("未完成"));
        Assert.IsTrue(invalid.MessageIsError); invalid.EnterDirect(); Assert.AreEqual("", invalid.Message); invalid.DirectEnergy = "0"; Press(Name(invalid), Key.Enter); Assert.IsFalse(invalid.MessageIsError); Assert.AreEqual(0L, store.FindEntry(invalid.Id)!.EnergyKcalTenths);
        var notes = Control<Expander>(row, "SheetDetailsExpander", 0); notes.IsExpanded = true; Pump(); var note = Control<TextBox>(row, "SheetNoteBox", 0); note.Focus(); note.Text = "第一行"; Press(note, Key.Enter); Assert.IsTrue(row.HasDraft); Assert.AreEqual("", store.FindEntry(row.Id)!.Note); Assert.IsFalse(Outside()); Assert.AreEqual("第一行", store.FindEntry(row.Id)!.Note);
        var date = Control<DatePicker>(row, "SheetDatePicker", 0); date.SelectedDate = null; name.Focus(); row.Note = "无效日期保持"; Assert.IsTrue(Outside()); Assert.AreEqual("第一行", store.FindEntry(row.Id)!.Note); date.SelectedDate = DateTime.Today; Assert.IsFalse(Outside());
        var suggested = New(); var foodInput = Control<FoodNameInput>(suggested, "SheetFoodInput", 0); var candidateName = (TextBox)foodInput.FindName("NameInput"); candidateName.Focus(); candidateName.Text = "明确WPF营养标签"; Pump();
        var popup = (Popup)foodInput.FindName("SuggestionsPopup"); var choices = (ListBox)foodInput.FindName("SuggestionsList"); Assert.IsTrue(popup.IsOpen); Press(candidateName, Key.Down); Press(choices, Key.Enter); Assert.IsFalse(popup.IsOpen); Assert.IsNull(store.FindEntry(suggested.Id)); Assert.AreEqual("kJ", suggested.UnitEnergyType);
        Control<TextBox>(suggested, "SheetWeightBox", 3).Text = "100"; Name(suggested).Focus(); Assert.IsFalse(Outside()); Assert.AreEqual(1000L, store.FindEntry(suggested.Id)!.EnergyKcalTenths);
        suggested.Weight = "125"; var unitCombo = Control<ComboBox>(suggested, "SheetUnitEnergyTypeBox", 4); unitCombo.Focus(); unitCombo.IsDropDownOpen = true; Press(unitCombo, Key.Enter); Assert.IsTrue(suggested.HasDraft); unitCombo.IsDropDownOpen = false; Name(suggested).Focus(); Assert.IsFalse(Outside());
        var failed = New(); Name(failed).Text = "明确自动保存事务失败"; failed.EnterDirect(); failed.DirectEnergy = "12";
        Sql("CREATE TRIGGER fail_auto_save BEFORE INSERT ON entries BEGIN SELECT RAISE(ABORT,'明确自动保存写入故障'); END;");
        Assert.IsTrue(Outside()); Assert.IsTrue(failed.HasDraft); Assert.IsNull(store.FindEntry(failed.Id)); Sql("DROP TRIGGER fail_auto_save"); Name(failed).Focus(); Assert.IsFalse(Outside()); Assert.IsNotNull(store.FindEntry(failed.Id));
        vm.SettingsFoodMode = 0; vm.SaveSettingsCommand.Execute(null);
        var asked = New(); Name(asked).Text = "明确自动保存询问"; asked.UnitEnergy = "123"; asked.Weight = "100";
        Assert.AreEqual(1, ConfirmationChecks.RespondChoice(() => Assert.IsTrue(Outside()), 2)); Assert.IsTrue(asked.HasDraft); Assert.IsNull(store.FindEntry(asked.Id));
        Assert.AreEqual(1, ConfirmationChecks.RespondChoice(() => Assert.IsFalse(Outside()), 7)); Assert.IsNotNull(store.FindEntry(asked.Id));
        vm.SettingsFoodMode = 2; vm.SettingsProteinEnabled = false; vm.SaveSettingsCommand.Execute(null); Pump(); Assert.IsFalse(vm.ProteinEnabled); Assert.IsFalse(journal.Settings().RecordsProtein);
        var proteinBefore = store.FindEntry(suggested.Id)!; var noProtein = New(); noProtein.UseFood(journal.Foods().Single(x => x.Id == proteinBefore.FoodId)); noProtein.Weight = "100"; Name(noProtein).Focus(); Assert.IsFalse(Outside()); Assert.IsNull(store.FindEntry(noProtein.Id)!.ProteinGramsHundredths);
        Assert.AreEqual(Visibility.Collapsed, GridFor(noProtein).Columns[6].Visibility);
        var noDirectProtein = New(); Name(noDirectProtein).Text = "明确关闭蛋白质直接输入"; noDirectProtein.EnterDirect(); noDirectProtein.DirectEnergy = "418.4"; noDirectProtein.DirectEnergyType = "kJ"; noDirectProtein.DirectEnergy = "418.4"; noDirectProtein.DirectProtein = "无效隐藏数值"; Press(Name(noDirectProtein), Key.Enter); Assert.IsNull(store.FindEntry(noDirectProtein.Id)!.ProteinGramsHundredths); Assert.AreEqual(1000L, store.FindEntry(noDirectProtein.Id)!.EnergyKcalTenths);
        journal.SaveFood(journal.Foods().Single(x => x.Id == proteinBefore.FoodId) with { ProteinPer100gHundredths = 2000 });
        suggested.Weight = "150"; Name(suggested).Focus(); Assert.IsFalse(Outside()); Assert.AreEqual(5000L, proteinBefore.ProteinPer100gHundredthsSnapshot); Assert.AreEqual(proteinBefore.ProteinPer100gHundredthsSnapshot, store.FindEntry(suggested.Id)!.ProteinPer100gHundredthsSnapshot); Assert.AreEqual(7500L, store.FindEntry(suggested.Id)!.ProteinGramsHundredths);
        suggested.EnterDirect(); suggested.DirectEnergy = "150"; Name(suggested).Focus(); Assert.IsTrue(Outside()); Assert.IsTrue(suggested.Message.Contains("启用蛋白质")); Assert.AreEqual("weight", store.FindEntry(suggested.Id)!.InputMode); suggested.Revert();
        var template = new TemplateViewModel(journal, DateTime.Today); template.Selected = template.Templates.First(); template.BeginReview(); template.ReviewMeal = template.Meals.First(); template.SaveBatch();
        var templateIds = template.ReviewItems.Where(x => x.Included).Select(x => x.EntryId).ToArray(); Assert.IsTrue(templateIds.Length > 0); Assert.IsTrue(templateIds.All(id => store.FindEntry(id)!.ProteinGramsHundredths is null));
        vm.SettingsDailyPage = 0; vm.SaveSettingsCommand.Execute(null); Pump(); Assert.IsFalse(vm.SheetMode); Assert.AreEqual(Visibility.Collapsed, ((DataGrid)window.FindName("EntriesGrid")).Columns[2].Visibility); Assert.IsFalse(((TextBox)window.FindName("UnitProteinBox")).IsVisible);
        vm.WeightMode = false; vm.EntryName = "明确关闭蛋白质表单"; vm.DirectKcal = "1"; vm.DirectProtein = "无效隐藏输入"; vm.SaveEntryCommand.Execute(null); Assert.IsNull(store.ReadSnapshot().Entries.Single(x => x.FoodNameSnapshot == "明确关闭蛋白质表单").ProteinGramsHundredths);
        vm.SelectedEntry = store.FindEntry(suggested.Id); vm.EditEntryCommand.Execute(null); vm.Weight = "200"; vm.SaveEntryCommand.Execute(null); Assert.AreEqual(10000L, store.FindEntry(suggested.Id)!.ProteinGramsHundredths); Assert.AreEqual(5000L, store.FindEntry(suggested.Id)!.ProteinPer100gHundredthsSnapshot);
        vm.SettingsDailyPage = 1; vm.SaveSettingsCommand.Execute(null); Pump();
        var layouts = new List<object>(); var page = (Grid)window.Content;
        Step("进入布局");
        foreach (var enabled in new[] { false, true })
        {
            vm.SettingsProteinEnabled = enabled; vm.SaveSettingsCommand.Execute(null); Pump();
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                Step($"布局开始 {enabled}/{scale}");
                window.Width = 920 * scale; window.Height = 600 * scale; page.LayoutTransform = new ScaleTransform(scale, scale); Pump();
                var grid = GridFor(noDirectProtein); Assert.AreEqual(2, grid.FrozenColumnCount); Assert.AreEqual("本项大卡", grid.Columns[1].Header); Assert.AreEqual("操作", grid.Columns.Last().Header); Assert.IsFalse(grid.CanUserReorderColumns);
                var scroll = Descendants<ScrollViewer>(grid).First(); scroll.ScrollToLeftEnd(); grid.ScrollIntoView(noDirectProtein); Pump(); Check(Control<TextBlock>(noDirectProtein, "SheetKcalPreview", 1), "前列");
                grid.ScrollIntoView(noDirectProtein, grid.Columns.Last()); Pump(); Check(Control<TextBlock>(noDirectProtein, "SheetKcalPreview", 7), "滚动后固定"); Check(Control<Button>(noDirectProtein, "SaveSheetRowButton", 7), "末列操作");
                foreach (var item in new[] { ("SheetWeightBox", 3, "100000.0"), ("SheetUnitEnergyBox", 4, "41840.00") })
                {
                    var box = Control<TextBox>(noProtein, item.Item1, item.Item2); box.ApplyTemplate(); Pump();
                    var host = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
                    var textWidth = new FormattedText(item.Item3, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch), box.FontSize, Brushes.Black, 1).Width;
                    Assert.IsGreaterThanOrEqualTo(textWidth, host.ActualWidth, $"{item.Item1}数值空间不足：{host.ActualWidth} < {textWidth}");
                }
                if (enabled)
                {
                    var proteinGrid = GridFor(noProtein); var input = Control<TextBox>(noProtein, "SheetUnitProteinBox", 6); var cell = Descendants<DataGridCell>(proteinGrid).Single(x => x.DataContext == noProtein && x.Column == proteinGrid.Columns[6]);
                    var inputBounds = input.TransformToAncestor(cell).TransformBounds(new Rect(input.RenderSize)); Assert.IsTrue(inputBounds.Top >= 0 && inputBounds.Bottom <= cell.ActualHeight);
                    var preview = Control<TextBlock>(noProtein, "SheetProteinPreview", 0); preview.BringIntoView(); Pump();
                    var bounds = preview.TransformToAncestor(window).TransformBounds(new Rect(preview.RenderSize));
                    Assert.IsTrue(preview.IsVisible && bounds.Height > 0 && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight, $"蛋白质详情不可见：{bounds}");
                }
                Step($"布局结束 {enabled}/{scale}");
                void Check(FrameworkElement control, string kind) { var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize)); Assert.IsTrue(control.IsVisible && bounds.Left >= 0 && bounds.Right <= window.ActualWidth && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight, bounds.ToString()); layouts.Add(new { ProteinEnabled = enabled, Scale = scale, Kind = kind, Bounds = bounds.ToString() }); }
            }
        }
        page.LayoutTransform = Transform.Identity; window.Width = 1240; window.Height = 820; vm.SettingsProteinEnabled = false; vm.SaveSettingsCommand.Execute(null); Pump();
        var navigationGrid = GridFor(noProtein); Assert.IsTrue(navigationGrid.EnableRowVirtualization); Assert.AreEqual(VirtualizationMode.Standard, VirtualizingPanel.GetVirtualizationMode(navigationGrid));
        var navigationPeer = new DataGridAutomationPeer(navigationGrid);
        for (var pass = 0; pass < 4; pass++)
        {
            navigationGrid.ScrollIntoView(navigationGrid.Items[navigationGrid.Items.Count - 1], navigationGrid.Columns.Last()); Pump(); Assert.IsNotNull(navigationPeer.GetChildren());
            navigationGrid.ScrollIntoView(navigationGrid.Items[0], navigationGrid.Columns[0]); Pump(); Assert.IsNotNull(navigationPeer.GetChildren());
        }
        Step("布局完成，检查草稿"); Assert.IsFalse(vm.HasSheetDrafts, string.Join(" / ", vm.SheetDays.SelectMany(x => x.Meals).SelectMany(x => x.Rows).Where(x => x.HasDraft).Select(x => x.Name + ":" + x.Message))); Assert.IsFalse(vm.ProteinEnabled);
        var full = store.ReadSnapshot(); var archivePath = Path.Combine(root, "protein-disabled.calorie-backup.json"); backups.Export(archivePath); journal.SaveSettings(journal.Settings() with { ProteinEnabled = null }); backups.Restore(backups.Inspect(archivePath)); Assert.IsFalse(journal.Settings().RecordsProtein); CollectionAssert.AreEqual(full.Entries, store.ReadSnapshot().Entries); CollectionAssert.AreEqual(full.Foods, store.ReadSnapshot().Foods);
        Step("准备关闭"); window.Close(); Step("已关闭"); var restarted = new MainViewModel(new JournalService(new SqliteJournalStore(store.DatabasePath)), backups, root); Assert.IsFalse(restarted.ProteinEnabled); Assert.IsTrue(restarted.SheetMode);
        JsonFiles.WriteNew(Path.Combine(root, "sheet-interaction-checks.json"), new { Success = true, EnterAndRepeatedEnter = true, OutsideClickAndNonFocusableTarget = true, EmptyAndInvalidNeverWritten = true, ZeroIsRecorded = true, InvalidDateRetainsDraft = true, CandidateAndComboDoNotSave = true, NotesKeepEnter = true, SqliteFailureRetainsDraft = true, AskCancelRetainsDraft = true, ProteinDisabledNewWeightDirectClassicTemplate = true, HistoricalProteinRetainedAndCalculated = true, ModeChangeCannotLoseProtein = true, SettingsRestartAndBackup = true, LayoutChecks = layouts });

        SheetEntryViewModel New() { lunch.AddCommand.Execute(null); Pump(); var value = lunch.Rows.Last(); Name(value).Focus(); return value; }
        void Step(string message) => File.AppendAllText(Path.Combine(root, "interaction-steps.log"), $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        TextBox Name(SheetEntryViewModel value) { var input = Control<FoodNameInput>(value, "SheetFoodInput", 0); input.FocusName(); Pump(); return (TextBox)input.FindName("NameInput"); }
        DataGrid GridFor(SheetEntryViewModel value) { var day = vm.SheetDays.Single(x => x.Meals.Any(g => g.Rows.Contains(value))); ((ListBox)sheet.FindName("SheetDaysList")).ScrollIntoView(day); Pump(); return Descendants<DataGrid>(sheet).Single(x => x.Items.Contains(value)); }
        T Control<T>(SheetEntryViewModel value, string name, int column) where T : FrameworkElement { var grid = GridFor(value); grid.SelectedItem = value; grid.ScrollIntoView(value, grid.Columns[column]); Pump(); return Descendants<T>(grid).First(x => x.Name == name && x.DataContext == value); }
        bool Outside(bool textTarget = false) { var target = textTarget ? (FrameworkElement)Descendants<TextBlock>(sheet).First(x => x.Text.StartsWith("新增一行后填写")) : (FrameworkElement)sheet.FindName("QuerySheetButton"); var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent }; target.RaiseEvent(args); Pump(); return args.Handled; }
        void Sql(string sql) { using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    }
    private static void Press(UIElement target, Key key) { target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Pump(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Descendants<T>(child)) yield return nested; } }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
}
