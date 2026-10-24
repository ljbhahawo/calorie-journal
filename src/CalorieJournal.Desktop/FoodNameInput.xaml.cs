using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CalorieJournal.Core;

namespace CalorieJournal.Desktop;

public partial class FoodNameInput : UserControl
{
    private bool choosing;
    public FoodNameInput() { InitializeComponent(); Unloaded += (_, _) => SuggestionsPopup.IsOpen = false; }
    public void FocusName() { NameInput.Focus(); NameInput.CaretIndex = NameInput.Text.Length; }
    public bool ChooseOnEnter()
    {
        if (SuggestionsPopup.IsOpen && SuggestionsList.SelectedItem is FoodItem) { Choose(); return true; }
        SuggestionsPopup.IsOpen = false; return false;
    }
    private void NameChanged(object sender, TextChangedEventArgs e) { if (!choosing && NameInput.IsKeyboardFocusWithin) Suggest(); }
    private void NameFocused(object sender, KeyboardFocusChangedEventArgs e) { if (!choosing) Suggest(); }
    private void Suggest()
    {
        if (DataContext is not SheetEntryViewModel row) return;
        try { SuggestionsList.ItemsSource = row.Suggest(NameInput.Text); SuggestionsList.SelectedIndex = -1; SuggestionsPopup.IsOpen = SuggestionsList.Items.Count > 0; }
        catch (Exception ex) when (ex is ValidationException or System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { SuggestionsPopup.IsOpen = false; row.Message = ex.Message; }
    }
    private void NameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && SuggestionsPopup.IsOpen && SuggestionsList.Items.Count > 0) { SuggestionsList.SelectedIndex = 0; SuggestionsList.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SuggestionsPopup.IsOpen = false; e.Handled = true; }
    }
    private void SuggestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Choose(); e.Handled = true; }
        else if (e.Key == Key.Escape) { choosing = true; try { SuggestionsPopup.IsOpen = false; NameInput.Focus(); } finally { choosing = false; } e.Handled = true; }
    }
    private void ChooseByMouse(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SuggestionsList, e.OriginalSource as DependencyObject) is ListBoxItem item) { SuggestionsList.SelectedItem = item.DataContext; Choose(); e.Handled = true; }
    }
    private void Choose()
    {
        if (SuggestionsList.SelectedItem is not FoodItem food || DataContext is not SheetEntryViewModel row) return;
        choosing = true;
        try { row.UseFood(food); SuggestionsPopup.IsOpen = false; NameInput.Focus(); NameInput.CaretIndex = NameInput.Text.Length; }
        catch (ValidationException ex) { row.Message = ex.Message; }
        finally { choosing = false; }
    }
    private void FocusLeft(object sender, KeyboardFocusChangedEventArgs e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
    { if (!NameInput.IsKeyboardFocusWithin && !SuggestionsList.IsKeyboardFocusWithin) SuggestionsPopup.IsOpen = false; }));
}
