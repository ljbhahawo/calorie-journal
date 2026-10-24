using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Threading;
using CalorieJournal.Core;

namespace CalorieJournal.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        MainTabs.Items.Remove(SettingsTab);
        MainTabs.Items.Add(SettingsTab);
        DataContext = viewModel;
        EntriesGrid.Columns[2].Visibility = viewModel.ProteinVisibility;
        viewModel.PropertyChanged += ProteinSettingChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= ProteinSettingChanged;
        viewModel.EntryValidationFailed += FocusMissingEntry;
        Closed += (_, _) => viewModel.EntryValidationFailed -= FocusMissingEntry;
        EntryDatePicker.SelectedDateChanged += (_, _) => viewModel.EntryDateInputValid = EntryDatePicker.SelectedDate is not null;
        EntryDatePicker.DateValidationError += (_, e) => { viewModel.EntryDateInputValid = false; e.ThrowException = false; };
        Activated += (_, _) => viewModel.RefreshClockDate();
        Closing += (_, e) => { if (!viewModel.IsBusy) SheetDailyPage.SaveActiveBeforeClose(); if (viewModel.IsBusy || !viewModel.CanClose()) e.Cancel = true; };
    }
    private void ProteinSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ProteinVisibility)) EntriesGrid.Columns[2].Visibility = ((MainViewModel)DataContext).ProteinVisibility;
    }
    private void DirectEnergyClicked(object sender, MouseButtonEventArgs e) => ((MainViewModel)DataContext).EnterDirectEnergy();
    private void DirectEnergyFocused(object sender, KeyboardFocusChangedEventArgs e) => ((MainViewModel)DataContext).EnterDirectEnergy();
    private void ValidateEntryDateInput(object sender, RoutedEventArgs e) => ((MainViewModel)DataContext).EntryDateInputValid = EntryDatePicker.SelectedDate is { } selected && DateTime.TryParse(EntryDatePicker.Text, out var typed) && selected.Date == typed.Date;
    private void FocusMissingEntry(EntryInputField field) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
    {
        Control control = field switch { EntryInputField.Date => EntryDatePicker, EntryInputField.Meal => EntryMealBox, EntryInputField.Name => EntryNameBox, EntryInputField.Weight => WeightBox, EntryInputField.UnitEnergy => UnitKcalBox, _ => DirectKcalBox };
        control.BringIntoView(); control.Focus();
        if (control is DatePicker date) { date.ApplyTemplate(); (date.Template.FindName("PART_TextBox", date) as Control)?.Focus(); }
    }));
    private void EntrySearchChanged(object sender, TextChangedEventArgs e) { if (EntrySearchBox.IsKeyboardFocusWithin) OpenEntrySearch(); }
    private void EntrySearchFocused(object sender, KeyboardFocusChangedEventArgs e) => OpenEntrySearch();
    private void OpenEntrySearch() => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (EntrySearchBox.IsKeyboardFocusWithin) { FoodChoiceBox.SelectedIndex = -1; EntrySearchPopup.IsOpen = true; } }));
    private void EntrySearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && EntrySearchPopup.IsOpen && FoodChoiceBox.Items.Count > 0) { FoodChoiceBox.SelectedIndex = 0; FoodChoiceBox.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EntrySearchPopup.IsOpen = false; e.Handled = true; }
    }
    private void EntryChoiceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ChooseEntryFood(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EntrySearchPopup.IsOpen = false; EntryNameBox.Focus(); e.Handled = true; }
    }
    private void EntryChoiceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FoodChoiceBox, e.OriginalSource as DependencyObject) is ListBoxItem item) { FoodChoiceBox.SelectedItem = item.DataContext; ChooseEntryFood(); e.Handled = true; }
    }
    private void ChooseEntryFood()
    {
        if (FoodChoiceBox.SelectedItem is not FoodItem food) return;
        ((MainViewModel)DataContext).ChosenFood = food; EntrySearchPopup.IsOpen = false; EntryNameBox.Focus();
    }
    private void EntrySearchFocusLeft(object sender, KeyboardFocusChangedEventArgs e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
    { if (!EntrySearchBox.IsKeyboardFocusWithin && !FoodChoiceBox.IsKeyboardFocusWithin) EntrySearchPopup.IsOpen = false; }));
}
