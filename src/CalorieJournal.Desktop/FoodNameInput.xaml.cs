using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CalorieJournal.Core;

namespace CalorieJournal.Desktop;

internal interface IEntryFoodLookup
{
    FoodItem[] Suggest(string query);
    void UseFood(FoodItem food);
    void ReportLookupError(string message);
}

public partial class FoodNameInput : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FoodNameInput),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty EditorStyleProperty = DependencyProperty.Register(
        nameof(EditorStyle), typeof(Style), typeof(FoodNameInput));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Style EditorStyle { get => (Style)GetValue(EditorStyleProperty); set => SetValue(EditorStyleProperty, value); }

    private bool choosing;
    public FoodNameInput()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => CloseSuggestions();
        Unloaded += (_, _) => CloseSuggestions();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseSuggestions(); };
    }
    public void FocusName() { NameInput.Focus(); NameInput.CaretIndex = NameInput.Text.Length; }
    public bool ChooseOnEnter()
    {
        if (SuggestionsPopup.IsOpen && SuggestionsList.SelectedItem is FoodItem) { Choose(); return true; }
        CloseSuggestions(); return false;
    }
    private void CloseSuggestions() { SuggestionsPopup.IsOpen = false; SuggestionsList.SelectedIndex = -1; }
    private void NameChanged(object sender, TextChangedEventArgs e)
    {
        if (choosing) return;
        if (IsVisible && NameInput.IsKeyboardFocusWithin) Suggest();
        else CloseSuggestions();
    }
    private void Suggest()
    {
        if (DataContext is not IEntryFoodLookup lookup || string.IsNullOrWhiteSpace(NameInput.Text)) { CloseSuggestions(); return; }
        try
        {
            SuggestionsList.ItemsSource = lookup.Suggest(NameInput.Text).Take(12).ToArray();
            SuggestionsList.SelectedIndex = -1;
            var hasMatches = SuggestionsList.Items.Count > 0;
            SuggestionsHint.Visibility = SuggestionsList.Visibility = hasMatches ? Visibility.Visible : Visibility.Collapsed;
            NoMatches.Visibility = hasMatches ? Visibility.Collapsed : Visibility.Visible;
            SuggestionsPopup.IsOpen = true;
        }
        catch (Exception ex) when (IsLookupError(ex)) { CloseSuggestions(); lookup.ReportLookupError(ex.Message); }
    }
    private void NameKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key is Key.Down or Key.Up)
        {
            if (!SuggestionsPopup.IsOpen && e.Key == Key.Down) Suggest();
            if (!SuggestionsPopup.IsOpen || SuggestionsList.Items.Count == 0) return;
            var step = e.Key == Key.Down ? 1 : -1;
            SuggestionsList.SelectedIndex = Math.Clamp(SuggestionsList.SelectedIndex + step, 0, SuggestionsList.Items.Count - 1);
            SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SuggestionsPopup.IsOpen) e.Handled = ChooseOnEnter();
        else if (e.Key == Key.Escape && SuggestionsPopup.IsOpen) { CloseSuggestions(); e.Handled = true; }
    }
    private void SuggestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Choose(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseSuggestions(); FocusName(); e.Handled = true; }
    }
    private void ChooseByMouse(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemsControl.ContainerFromElement(SuggestionsList, e.OriginalSource as DependencyObject) is ListBoxItem item) { SuggestionsList.SelectedItem = item.DataContext; Choose(); e.Handled = true; }
    }
    private void Choose()
    {
        if (SuggestionsList.SelectedItem is not FoodItem food || DataContext is not IEntryFoodLookup lookup) return;
        choosing = true;
        try { lookup.UseFood(food); CloseSuggestions(); FocusName(); }
        catch (Exception ex) when (IsLookupError(ex)) { CloseSuggestions(); FocusName(); lookup.ReportLookupError(ex.Message); }
        finally { choosing = false; }
    }
    private static bool IsLookupError(Exception ex) => ex is ValidationException or System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException;
    private void FocusLeft(object sender, KeyboardFocusChangedEventArgs e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
    { if (!NameInput.IsKeyboardFocusWithin && !SuggestionsList.IsKeyboardFocusWithin) CloseSuggestions(); }));
}
