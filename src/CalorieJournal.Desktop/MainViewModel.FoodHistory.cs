using System.Collections.ObjectModel;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalorieJournal.Desktop;

public sealed record FoodHistoryRow(IntakeEntry Entry)
{
    public string TimeText => Show(Entry.LocalTime);
    public string WeightText => Entry.WeightGramsTenths is { } grams ? $"{grams / 10m:0.#} g" : "—";
    public string PreparationText => Show(Entry.PreparationSnapshot);
    public string NoteText => Show(Entry.Note);
    public string SourceText => Show(Entry.SourceSnapshot);

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}

public sealed partial class MainViewModel
{
    private string appliedFoodHistoryQuery = "";
    private int foodHistoryPage = 1;
    private bool foodHistoryHasNext;

    public ObservableCollection<FoodHistoryRow> FoodHistoryRows { get; } = [];
    [ObservableProperty] private int historyViewIndex;
    [ObservableProperty] private string foodHistoryQuery = "";
    [ObservableProperty] private FoodHistoryRow? selectedFoodHistory;
    [ObservableProperty] private string foodHistorySummary = "";

    partial void OnHistoryViewIndexChanged(int value)
    {
        if (value == 1 && TabIndex == 2) Run(RefreshFoodHistory);
    }

    partial void OnFoodHistoryQueryChanged(string value)
    {
        if (value.Length == 0 && appliedFoodHistoryQuery.Length > 0) QueryFoodHistory();
    }

    [RelayCommand]
    private void QueryFoodHistory() => Run(() => LoadFoodHistory(FoodHistoryQuery.Trim(), 1));

    private bool CanPreviousFoodHistory() => foodHistoryPage > 1;
    private bool CanNextFoodHistory() => foodHistoryHasNext;

    [RelayCommand(CanExecute = nameof(CanPreviousFoodHistory))]
    private void PreviousFoodHistory() => Run(() => LoadFoodHistory(appliedFoodHistoryQuery, foodHistoryPage - 1));

    [RelayCommand(CanExecute = nameof(CanNextFoodHistory))]
    private void NextFoodHistory() => Run(() => LoadFoodHistory(appliedFoodHistoryQuery, foodHistoryPage + 1));

    private void RefreshFoodHistory() => LoadFoodHistory(appliedFoodHistoryQuery, foodHistoryPage, SelectedFoodHistory?.Entry.Id);

    private void LoadFoodHistory(string query, int page, Guid? selectedId = null)
    {
        var result = journal.SearchIntakeHistory(query, page);
        if (page > 1 && result.Entries.Length == 0)
        {
            page = 1;
            result = journal.SearchIntakeHistory(query, page);
        }

        appliedFoodHistoryQuery = query;
        foodHistoryPage = page;
        foodHistoryHasNext = result.HasNext;
        Fill(FoodHistoryRows, result.Entries.Select(entry => new FoodHistoryRow(entry)));
        SelectedFoodHistory = FoodHistoryRows.FirstOrDefault(row => row.Entry.Id == selectedId);
        var scope = query.Length == 0 ? "全部摄入" : $"「{query}」";
        FoodHistorySummary = result.Entries.Length == 0
            ? (query.Length == 0 ? "尚无已保存的摄入记录。" : $"{scope}没有匹配的摄入记录。")
            : $"{scope} · 第 {page} 页 · 本页 {result.Entries.Length} 条";
        PreviousFoodHistoryCommand.NotifyCanExecuteChanged();
        NextFoodHistoryCommand.NotifyCanExecuteChanged();
    }
}
