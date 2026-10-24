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
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class FormExperienceChecks
{
    public static void Verify()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "wpf_form_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        var journal = new JournalService(store); journal.SaveSettings(JournalSettings.Default with { NewFoodMode = "never" });
        var vm = new MainViewModel(journal, new BackupService(store, Path.Combine(root, "recovery")), root);
        var window = new MainWindow(vm); window.Show(); window.Activate(); Pump();
        vm.EntryMeal = null; SaveMissing("EntryMealBox"); vm.EntryMeal = journal.Meals()[0]; SaveMissing("EntryNameBox");
        vm.EntryName = "明确表单重量零热量"; Assert.AreEqual("", vm.EntryValidationMessage); SaveMissing("WeightBox"); vm.Weight = "100"; SaveMissing("UnitKcalBox");
        vm.UnitKcal = "0"; Save(); Assert.HasCount(1, store.ReadSnapshot().Entries); Assert.AreEqual(0, store.ReadSnapshot().Entries[0].EnergyKcalTenths);
        vm.EnterDirectEnergy(); vm.EntryName = "明确表单直接零"; SaveMissing("DirectKcalBox"); SaveMissing("DirectKcalBox");
        vm.DirectKcal = "0"; Save(); Assert.HasCount(2, store.ReadSnapshot().Entries);
        var date = (DatePicker)window.FindName("EntryDatePicker"); date.SelectedDate = null; Pump(); Save();
        Assert.IsTrue(date.IsKeyboardFocusWithin); Assert.IsTrue(vm.EntryValidationMessage.Contains("日期")); Assert.HasCount(2, store.ReadSnapshot().Entries);
        date.SelectedDate = DateTime.Today; vm.NewEntryCommand.Execute(null); Pump();
        var search = (TextBox)window.FindName("EntrySearchBox"); search.BringIntoView(); search.Focus(); search.Text = "米饭"; Pump();
        var popup = (Popup)window.FindName("EntrySearchPopup"); var choices = (ListBox)window.FindName("FoodChoiceBox");
        Assert.IsTrue(popup.IsOpen); Assert.IsGreaterThan(0, choices.Items.Count); Assert.AreEqual("", vm.EntryName);
        Press(search, Key.Down); Assert.AreEqual("", vm.EntryName); Press(choices, Key.Escape); Assert.IsFalse(popup.IsOpen); Assert.AreEqual("", vm.EntryName);
        search.Focus(); search.Text = "长粒白米饭"; Pump(); Press(search, Key.Down); Press(choices, Key.Enter);
        Assert.AreEqual("长粒白米饭（熟）", vm.EntryName); Assert.IsFalse(popup.IsOpen); Assert.IsTrue(vm.EntrySource.Contains("USDA")); Assert.HasCount(2, store.ReadSnapshot().Entries);
        search.Focus(); search.Text = "明确不存在的食物关键词"; Pump(); Assert.IsTrue(popup.IsOpen); Assert.AreEqual(0, choices.Items.Count); Assert.IsTrue(((TextBlock)window.FindName("EntrySearchEmpty")).IsVisible); Press(search, Key.Escape);
        var checks = new List<object>(); var page = (Grid)window.Content;
        foreach (var scale in new[] {1.0,1.25,1.5,2.0})
        {
            window.Width = 920 * scale; window.Height = 600 * scale; page.LayoutTransform = new ScaleTransform(scale, scale); Pump();
            foreach (var name in new[] {"EntryNameBox","EntryNoteBox"})
            {
                var box = (TextBox)window.FindName(name); box.Text = "居中与清空检查"; box.Height = name == "EntryNoteBox" ? 80 : 46; box.BringIntoView(); box.ApplyTemplate(); Pump();
                var clear = (Button)box.Template.FindName("ClearTextButton", box); var bounds = clear.TransformToAncestor(box).TransformBounds(new Rect(clear.RenderSize));
                Assert.IsLessThan(1.0, Math.Abs(bounds.Top + bounds.Height / 2 - box.ActualHeight / 2));
                Assert.IsTrue(clear.IsVisible); checks.Add(new {Scale=scale,Control=name,CenterError=Math.Abs(bounds.Top + bounds.Height/2-box.ActualHeight/2)});
            }
        }
        page.LayoutTransform = Transform.Identity; window.Width = 1240; window.Height = 820;
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => window.Close(), true));
        JsonFiles.WriteNew(Path.Combine(root, "form-experience-checks.json"), new { Success=true, RequiredFocusAndScroll=true, RepeatedSubmissionRetainsDraft=true, EmptyDateRejected=true, ExplicitZeroAccepted=true, SearchKeyboardAndCancel=true, UnmatchedSearchVisible=true, SourceRetained=true, ClearCenteredSingleAndMultiline=true, LayoutChecks=checks });
        void Save() { ((IInvokeProvider)new ButtonAutomationPeer((Button)window.FindName("SaveEntryButton")).GetPattern(PatternInterface.Invoke)).Invoke(); Pump(); }
        void SaveMissing(string name) { var count=store.ReadSnapshot().Entries.Length; Save(); var box=(Control)window.FindName(name); Assert.IsTrue(box.IsKeyboardFocusWithin, name+"没有获得焦点"); var rect=box.TransformToAncestor(window).TransformBounds(new Rect(box.RenderSize)); Assert.IsTrue(rect.Top>=0 && rect.Bottom<=window.ActualHeight); Assert.IsTrue(vm.EntryValidationMessage.Length>0); Assert.AreEqual(count,store.ReadSnapshot().Entries.Length); }
    }
    private static void Press(UIElement element, Key key) { element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), 0, key) {RoutedEvent=Keyboard.PreviewKeyDownEvent}); Pump(); }
    private static void Pump() {var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
}
