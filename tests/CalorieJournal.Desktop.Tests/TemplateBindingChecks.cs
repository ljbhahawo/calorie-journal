using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class TemplateBindingChecks
{
    public static void Verify()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "wpf_template_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(directory, "journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        var journal = new JournalService(store); var food = journal.Foods("长粒白米饭").Single();
        var portion = new FoodPortion { Id = Guid.NewGuid(), FoodId = food.Id, Name = "一碗", GramsTenths = 1500 }; journal.SavePortion(portion);
        var vm = new TemplateViewModel(journal, DateTime.Today); var window = new TemplateWindow(vm); window.Show(); Pump();
        ((TextBox)window.FindName("TemplateNameBox")).Text = "常用午餐";
        ((ComboBox)window.FindName("TemplateMealBox")).SelectedItem = vm.Meals[1];
        ((TextBox)window.FindName("TemplateFoodQueryBox")).Text = "米饭"; Pump();
        ((ComboBox)window.FindName("TemplateFoodChoiceBox")).SelectedItem = vm.Foods.Single(x => x.Id == food.Id); Click("AddTemplateFoodButton");
        var row = vm.EditItems.Single(); row.Unit = "份"; row.Amount = "1.5";
        ((TextBox)window.FindName("TemplateFoodQueryBox")).Text = "香蕉"; Pump();
        ((ComboBox)window.FindName("TemplateFoodChoiceBox")).SelectedItem = vm.Foods.First(); Click("AddTemplateFoodButton");
        Click("SaveTemplateButton"); Assert.HasCount(1, journal.Templates()); Assert.IsEmpty(store.ReadSnapshot().Entries);
        ((TextBox)window.FindName("TemplateNameBox")).Text = "常用午餐调整";
        // 实际绑定的筛选清空列表不能触发丢弃确认或清空当前编辑内容。
        ((TextBox)window.FindName("TemplateQueryBox")).Text = "无匹配模板"; Pump(); Assert.AreEqual("常用午餐调整", vm.Name); Assert.HasCount(2, vm.EditItems);
        ((TextBox)window.FindName("TemplateQueryBox")).Text = ""; Pump();
        Assert.AreEqual(1, ConfirmationChecks.Respond(vm.New, false)); Assert.AreEqual("常用午餐调整", vm.Name);
        Click("SaveTemplateButton"); Assert.AreEqual("常用午餐调整", journal.Templates().Single().Name);
        var layout = Layout(window, new[] { "NewTemplateButton", "SaveTemplateButton", "BeginTemplateReviewButton", "DeleteTemplateButton", "CloseTemplatesButton" });
        Click("BeginTemplateReviewButton"); Assert.HasCount(2, vm.ReviewItems); Assert.IsEmpty(store.ReadSnapshot().Entries);
        vm.ReviewItems[1].Included = false; Pump(); Assert.IsTrue(((TextBlock)window.FindName("TemplateReviewTotalText")).Text.Contains("292.5"));
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => Click("CancelTemplateReviewButton"), true)); Assert.IsEmpty(store.ReadSnapshot().Entries);
        Click("BeginTemplateReviewButton"); vm.ReviewItems[0].Amount = "2"; vm.ReviewItems[1].Amount = "80";
        ((TextBox)window.FindName("TemplateReviewNoteBox")).Text = "本次整餐核对"; Pump();
        layout.AddRange(Layout(window, new[] { "SaveTemplateBatchButton", "CancelTemplateReviewButton", "CloseTemplatesButton" }));
        Click("SaveTemplateBatchButton"); Assert.IsTrue(vm.BatchSaved); Assert.IsFalse(((Button)window.FindName("SaveTemplateBatchButton")).IsEnabled);
        var saved = store.ReadSnapshot(); Assert.HasCount(2, saved.Entries); Assert.HasCount(1, saved.TemplateBatches!);
        Assert.Throws<ValidationException>(vm.SaveBatch); Assert.HasCount(2, store.ReadSnapshot().Entries);
        var originalEntries = JsonSerializer.Serialize(saved.Entries);
        journal.SavePortion(portion with { GramsTenths = 2000 }); journal.SaveFood(food with { Name = "长粒白米饭核对", KcalPer100gHundredths = 20000, UserModified = true });
        vm.TabIndex = 0; Click("BeginTemplateReviewButton"); Assert.AreEqual("长粒白米饭核对", vm.ReviewItems[0].FoodName);
        Assert.AreEqual(3000L, vm.ReviewItems[0].CreateItem().WeightGramsTenths);
        Assert.AreEqual(originalEntries, JsonSerializer.Serialize(store.ReadSnapshot().Entries));
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => Click("CancelTemplateReviewButton"), true));
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => Click("DeleteTemplateButton"), false)); Assert.HasCount(1, journal.Templates());
        window.Close(); Pump();
        var reopened = new SqliteJournalStore(store.DatabasePath); reopened.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        Assert.HasCount(1, reopened.ReadTemplates()); Assert.HasCount(1, reopened.ReadTemplateBatches()); Assert.HasCount(2, reopened.ReadSnapshot().Entries);
        JsonFiles.WriteNew(Path.Combine(directory, "template-checks.json"), new { Success = true, TemplateCrudAndFilterProtectDraft = true, CancelDoesNotWrite = true,
            WholeMealSave = true, RepeatedSaveRejected = true, CurrentReferencesAndHistoricalSnapshots = true, Restart = true, LayoutChecks = layout });

        void Click(string name) { ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); }
    }
    private static List<object> Layout(Window window, string[] names)
    {
        var checks = new List<object>(); var grid = (Grid)window.Content;
        foreach (var factor in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            window.Width = window.MinWidth * factor; window.Height = window.MinHeight * factor; grid.LayoutTransform = new ScaleTransform(factor, factor); Pump();
            foreach (var name in names)
            {
                var control = (Button)window.FindName(name); control.BringIntoView(); Pump();
                var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
                Assert.IsTrue(control.IsVisible && bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight + 1, $"模板窗口{factor}下{name}不可达：{bounds}");
                checks.Add(new { Window = window.Title, Scale = factor, Control = name });
            }
        }
        grid.LayoutTransform = Transform.Identity; window.Width = 1080; window.Height = 790; Pump(); return checks;
    }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
}
