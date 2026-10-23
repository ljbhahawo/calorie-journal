using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalorieJournal.Desktop;

public sealed partial class PortionEditorViewModel : ObservableObject
{
    private readonly JournalService journal;
    private readonly Guid foodId;
    private FoodPortion? editing;
    private FoodPortion? selected;
    private bool loading;
    private bool dirty;
    public string FoodName { get; }
    public ObservableCollection<FoodPortion> Portions { get; } = [];
    public string[] Units { get; } = ["g", "kg"];
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string grams = "";
    [ObservableProperty] private string unit = "g";
    [ObservableProperty] private string status = "每份克数使用实际称量值，不自动推测大小或密度。";
    public FoodPortion? Selected
    {
        get => selected;
        set
        {
            if (value?.Id == selected?.Id) return;
            if (!CanClose()) { OnPropertyChanged(); return; }
            SetProperty(ref selected, value); Load(value);
        }
    }
    public PortionEditorViewModel(JournalService journal, Guid foodId)
    {
        this.journal = journal; this.foodId = foodId;
        FoodName = journal.Foods().Single(x => x.Id == foodId).Name;
        Refresh();
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!loading && e.PropertyName is nameof(Name) or nameof(Grams) or nameof(Unit)) dirty = true;
    }
    private void Refresh() { Portions.Clear(); foreach (var value in journal.Portions(foodId)) Portions.Add(value); }
    private void Load(FoodPortion? value)
    {
        loading = true; editing = value; Name = value?.Name ?? ""; Grams = (value?.GramsTenths / 10m)?.ToString("0.0", CultureInfo.InvariantCulture) ?? ""; Unit = "g";
        loading = false; dirty = false;
    }
    public bool CanClose() => !dirty || MessageBox.Show("份量内容尚未保存，是否放弃修改？", "请确认", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    public void New()
    {
        if (!CanClose()) return;
        selected = null; OnPropertyChanged(nameof(Selected)); Load(null);
    }
    public void Save()
    {
        var value = (editing ?? new FoodPortion { Id = Guid.NewGuid(), FoodId = foodId, Name = "", GramsTenths = 1 }) with
        { Name = Name, GramsTenths = WeightCalculator.Grams(Validation.Number(Grams, "每份重量"), Unit, "每份重量") };
        journal.SavePortion(value); dirty = false; selected = null; OnPropertyChanged(nameof(Selected)); Load(null); Refresh();
        Status = "份量已保存，历史摄入仍使用保存时的数值。";
    }
    public void Delete()
    {
        Validation.Require(editing is not null, "请先选择一个已保存份量。");
        if (MessageBox.Show($"删除“{editing.Name}”？历史摄入及其换算依据保留。", "请确认", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        journal.SavePortion(editing with { IsDeleted = true }); dirty = false; selected = null; OnPropertyChanged(nameof(Selected)); Load(null); Refresh();
        Status = "份量已删除，历史计算依据保留。";
    }
}
