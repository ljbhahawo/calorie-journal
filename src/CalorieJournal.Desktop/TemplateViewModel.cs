using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalorieJournal.Desktop;

public sealed partial class TemplateLineViewModel : ObservableObject
{
    private readonly JournalService journal;
    private readonly TemplateItem original;
    private readonly FoodItem? currentFood;
    public Guid EntryId { get; } = Guid.NewGuid();
    public string FoodName => currentFood?.Name ?? original.FoodNameSnapshot + "（已删除，请更换）";
    public string FoodDetail => currentFood is null ? original.PreparationSnapshot : $"{currentFood.Preparation} · {currentFood.KcalPer100g:0.00}大卡/100g · 蛋白质 {currentFood.ProteinText}/100g · {currentFood.SourceSummary}";
    public string[] Units { get; } = ["g", "kg", "份"];
    public ObservableCollection<PortionOption> Portions { get; } = [];
    [ObservableProperty] private string amount = "100";
    [ObservableProperty] private string unit = "g";
    [ObservableProperty] private bool included = true;
    [ObservableProperty] private PortionOption? selectedPortion;
    public string Preview
    {
        get { try { var item = CreateItem(); return $"{item.WeightGramsTenths / 10m:0.0}g · {Nutrition.Energy(item.WeightGramsTenths, item.KcalPer100gHundredthsSnapshot, item.KilojoulesPer100gHundredthsSnapshot) / 10m:0.0}大卡 · 蛋白质 {Nutrition.ProteinText(Nutrition.Protein(item.WeightGramsTenths, item.ProteinPer100gHundredthsSnapshot))}"; }
            catch (ValidationException exception) { return exception.Message; } }
    }
    public TemplateLineViewModel(JournalService journal, TemplateItem item)
    {
        this.journal = journal; original = item;
        currentFood = journal.Foods().SingleOrDefault(x => x.Id == item.FoodId);
        foreach (var portion in journal.Portions(item.FoodId)) Portions.Add(new(portion.Id, portion.Name, portion.GramsTenths));
        if (item.WeightCalculation is { Kind: "portion" } calculation)
        {
            Unit = "份"; Amount = (calculation.PortionCountThousandths!.Value / 1000m).ToString("0.###", CultureInfo.InvariantCulture);
            SelectedPortion = Portions.FirstOrDefault(x => x.Id == calculation.PortionId);
        }
        else { Amount = (item.WeightGramsTenths / 10m).ToString("0.0", CultureInfo.InvariantCulture); SelectedPortion = Portions.FirstOrDefault(); }
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Amount) or nameof(Unit) or nameof(SelectedPortion)) OnPropertyChanged(nameof(Preview));
    }
    public TemplateItem CreateItem()
    {
        WeightCalculationSnapshot? calculation = null;
        long grams;
        if (Unit == "份")
        {
            Validation.Require(SelectedPortion is not null, $"“{FoodName}”的份量已失效或未选择，请重新选择。");
            calculation = new WeightCalculationSnapshot { Kind = "portion", PortionId = SelectedPortion.Id, PortionName = SelectedPortion.Name,
                PortionGramsTenths = SelectedPortion.GramsTenths, PortionCountThousandths = Validation.Scaled(Validation.Number(Amount, "份数"), 1000, 0.001m, 100000, "份数") };
            grams = calculation.NetGramsTenths;
        }
        else grams = WeightCalculator.Grams(Validation.Number(Amount, "重量"), Unit, "可食重量");
        return journal.CreateTemplateItem(original.Id, original.FoodId, grams, calculation);
    }
}

public sealed partial class TemplateViewModel : ObservableObject
{
    private readonly JournalService journal;
    private MealTemplate? editing;
    private MealTemplate? selected;
    private bool loading;
    private bool dirty;
    private Guid? batchId;
    private Guid? batchTemplateId;
    public bool SavedAnyBatch { get; private set; }
    public ObservableCollection<MealTemplate> Templates { get; } = [];
    public ObservableCollection<FoodItem> Foods { get; } = [];
    public ObservableCollection<MealCategory> Meals { get; } = [];
    public ObservableCollection<TemplateLineViewModel> EditItems { get; } = [];
    public ObservableCollection<TemplateLineViewModel> ReviewItems { get; } = [];
    [ObservableProperty] private string query = "";
    [ObservableProperty] private string foodQuery = "";
    [ObservableProperty] private FoodItem? selectedFood;
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string note = "";
    [ObservableProperty] private MealCategory? preferredMeal;
    [ObservableProperty] private MealCategory? reviewMeal;
    [ObservableProperty] private DateTime reviewDate;
    [ObservableProperty] private string reviewNote = "";
    [ObservableProperty] private TemplateLineViewModel? selectedEditItem;
    [ObservableProperty] private TemplateLineViewModel? selectedReviewItem;
    [ObservableProperty] private int tabIndex;
    [ObservableProperty] private bool batchSaved;
    [ObservableProperty] private string status = "保存模板不会记录摄入；带入整餐后，请按当前资料核对再保存。";
    public bool CanEditReview => batchId is not null && !BatchSaved;
    public string ReviewTotal
    {
        get
        {
            try
            {
                var items = ReviewItems.Where(x => x.Included).Select(x => x.CreateItem()).ToArray();
                var proteins = items.Select(x => Nutrition.Protein(x.WeightGramsTenths, x.ProteinPer100gHundredthsSnapshot)).ToArray();
                var summary = proteins.Any(x => x is not null) ? $"蛋白质已记录 {proteins.Sum(x => x ?? 0) / 100m:0.00} g · {proteins.Count(x => x is null)}项未填写" : "蛋白质未填写";
                return items.Length == 0 ? "尚未选择食物" : $"已选 {items.Length} 项 · 合计 {items.Sum(x => Nutrition.Energy(x.WeightGramsTenths, x.KcalPer100gHundredthsSnapshot, x.KilojoulesPer100gHundredthsSnapshot)) / 10m:0.0} 大卡 · {summary}";
            }
            catch (ValidationException exception) { return "请修正明细：" + exception.Message; }
        }
    }
    public MealTemplate? Selected
    {
        get => selected;
        set
        {
            if (loading) return;
            if (value?.Id == selected?.Id) return;
            if (!ConfirmEditDiscard()) { OnPropertyChanged(); return; }
            SetProperty(ref selected, value); Load(value);
        }
    }
    public TemplateViewModel(JournalService journal, DateTime date)
    {
        this.journal = journal; ReviewDate = date;
        foreach (var meal in journal.Meals().Where(x => x.IsActive)) Meals.Add(meal);
        ReviewMeal = Meals.FirstOrDefault(); RefreshTemplates(); RefreshFoods();
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(BatchSaved)) OnPropertyChanged(nameof(CanEditReview));
        if (e.PropertyName == nameof(Query)) RefreshTemplates();
        if (e.PropertyName == nameof(FoodQuery)) RefreshFoods();
        if (!loading && e.PropertyName is nameof(Name) or nameof(Note) or nameof(PreferredMeal)) dirty = true;
    }
    private static bool Confirm(string message) => MessageBox.Show(message, "请确认", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    private bool ConfirmEditDiscard() => !dirty || Confirm("模板修改尚未保存，是否放弃修改？");
    public bool CanClose() => (!dirty && !CanEditReview) || Confirm("有未保存的模板或整餐核对内容，是否放弃并关闭？");
    private void RefreshTemplates()
    {
        // 筛选时保留编辑对象；列表暂时隐藏所选模板不丢弃编辑内容。
        if (journal is null) return;
        var current = selected; loading = true; Templates.Clear(); foreach (var value in journal.Templates(Query)) Templates.Add(value);
        selected = current is null ? null : Templates.FirstOrDefault(x => x.Id == current.Id);
        OnPropertyChanged(nameof(Selected)); loading = false;
    }
    private void RefreshFoods()
    {
        if (journal is null) return;
        Foods.Clear(); foreach (var food in journal.Foods(FoodQuery)) Foods.Add(food); SelectedFood = null;
    }
    private void AddLine(ObservableCollection<TemplateLineViewModel> target, TemplateItem item)
    {
        var line = new TemplateLineViewModel(journal, item); line.PropertyChanged += LineChanged; target.Add(line);
    }
    private void ClearLines(ObservableCollection<TemplateLineViewModel> target)
    { foreach (var line in target) line.PropertyChanged -= LineChanged; target.Clear(); }
    private void LineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!loading && sender is TemplateLineViewModel line && EditItems.Contains(line)) dirty = true;
        OnPropertyChanged(nameof(ReviewTotal));
    }
    private void Load(MealTemplate? value)
    {
        loading = true; editing = value; Name = value?.Name ?? ""; Note = value?.Note ?? "";
        PreferredMeal = Meals.FirstOrDefault(x => x.Id == value?.PreferredMealId); ClearLines(EditItems);
        if (value is not null) foreach (var item in value.Items) AddLine(EditItems, item);
        loading = false; dirty = false;
        Status = value?.PreferredMealId is not null && PreferredMeal is null ? "默认餐次已停用，请选择启用餐次或不指定。" : "模板明细按当前食物与份量核对；历史摄入保持原快照。";
    }
    public void New()
    {
        if (!ConfirmEditDiscard()) return;
        selected = null; OnPropertyChanged(nameof(Selected)); Load(null); TabIndex = 0;
    }
    public void ClearPreferredMeal() => PreferredMeal = null;
    public void AddFood(bool replace)
    {
        Validation.Require(SelectedFood is not null, "请先搜索并选择个人库中的食物。");
        Validation.Require(TabIndex == 0 || CanEditReview, "请先带入一个已保存模板。");
        var target = TabIndex == 0 ? EditItems : ReviewItems; var current = TabIndex == 0 ? SelectedEditItem : SelectedReviewItem;
        Validation.Require(!replace || current is not null, "请先选择要更换的明细行。");
        Validation.Require(replace || target.Count < 100, "每餐最多100项食物。");
        var item = journal.CreateTemplateItem(Guid.NewGuid(), SelectedFood.Id, 1000);
        var index = current is null ? target.Count : target.IndexOf(current);
        if (replace && current is not null) { current.PropertyChanged -= LineChanged; target.Remove(current); }
        var line = new TemplateLineViewModel(journal, item); line.PropertyChanged += LineChanged;
        if (replace) target.Insert(index, line); else target.Add(line);
        if (TabIndex == 0) { SelectedEditItem = line; dirty = true; } else SelectedReviewItem = line;
        OnPropertyChanged(nameof(ReviewTotal));
    }
    public void RemoveLine()
    {
        var target = TabIndex == 0 ? EditItems : ReviewItems; var row = TabIndex == 0 ? SelectedEditItem : SelectedReviewItem;
        Validation.Require(TabIndex == 0 || CanEditReview, "当前整餐不能修改。");
        Validation.Require(row is not null, "请先选择一行草稿。");
        row.PropertyChanged -= LineChanged; target.Remove(row);
        if (TabIndex == 0) dirty = true; OnPropertyChanged(nameof(ReviewTotal));
    }
    public void Save()
    {
        var value = new MealTemplate { Id = editing?.Id ?? Guid.NewGuid(), Name = Name, Note = Note, PreferredMealId = PreferredMeal?.Id,
            CreatedAtUtc = editing?.CreatedAtUtc ?? DateTimeOffset.UtcNow, Items = EditItems.Select(x => x.CreateItem()).ToArray() };
        journal.SaveTemplate(value); dirty = false; selected = null; OnPropertyChanged(nameof(Selected));
        RefreshTemplates(); Selected = Templates.FirstOrDefault(x => x.Id == value.Id); if (Selected is null) Load(value);
        Status = "模板已保存，尚未增加摄入；可以带入整餐核对。";
    }
    public void Delete()
    {
        Validation.Require(editing is not null, "请先选择已保存模板。");
        if (!Confirm($"删除模板“{editing.Name}”？已保存的摄入和批次保留。")) return;
        journal.SaveTemplate(editing with { IsDeleted = true }); dirty = false; selected = null; OnPropertyChanged(nameof(Selected)); Load(null); RefreshTemplates();
        Status = "模板已删除，历史摄入保持不变。";
    }
    public void BeginReview()
    {
        Validation.Require(editing is not null && !dirty, "请先保存模板，再带入整餐。");
        if (CanEditReview && !Confirm("已有尚未保存的整餐，是否放弃并重新带入？")) return;
        var current = journal.Templates().SingleOrDefault(x => x.Id == editing.Id);
        Validation.Require(current is not null, "模板已删除，请重新选择。");
        var items = journal.RefreshTemplateItems(current);
        ClearLines(ReviewItems); foreach (var item in items) AddLine(ReviewItems, item);
        batchId = Guid.NewGuid(); batchTemplateId = current.Id; BatchSaved = false;
        ReviewMeal = Meals.FirstOrDefault(x => x.Id == current.PreferredMealId); ReviewNote = current.Note; TabIndex = 1;
        OnPropertyChanged(nameof(CanEditReview)); OnPropertyChanged(nameof(ReviewTotal));
        Status = "已按当前食物和份量资料带入，请核对实际食用量并选择餐次；尚未保存摄入。";
    }
    public void CancelReview()
    {
        if (CanEditReview && !Confirm("放弃本次整餐核对？尚未保存的摄入不会写入。")) return;
        ClearLines(ReviewItems); batchId = null; batchTemplateId = null; BatchSaved = false; OnPropertyChanged(nameof(CanEditReview)); OnPropertyChanged(nameof(ReviewTotal));
        TabIndex = 0; Status = "整餐核对已关闭。";
    }
    public void SaveBatch()
    {
        Validation.Require(CanEditReview && batchId is not null && batchTemplateId is not null, "本批已保存或未带入模板，请重新带入后再记录。");
        Validation.Require(ReviewMeal is not null, "请选择本次摄入的启用餐次。");
        var entries = ReviewItems.Where(x => x.Included).Select(row =>
        {
            var item = row.CreateItem();
            return new IntakeEntry { Id = row.EntryId, EntryDate = DateOnly.FromDateTime(ReviewDate), MealCategoryId = ReviewMeal.Id, MealNameSnapshot = ReviewMeal.Name,
                FoodId = item.FoodId, FoodNameSnapshot = item.FoodNameSnapshot, PreparationSnapshot = item.PreparationSnapshot, InputMode = "weight",
                WeightGramsTenths = item.WeightGramsTenths, KcalPer100gHundredthsSnapshot = item.KcalPer100gHundredthsSnapshot,
                EnergyKcalTenths = Nutrition.Energy(item.WeightGramsTenths, item.KcalPer100gHundredthsSnapshot, item.KilojoulesPer100gHundredthsSnapshot), WeightCalculation = item.WeightCalculation,
                KilojoulesHundredthsSnapshot = item.KilojoulesPer100gHundredthsSnapshot, ProteinPer100gHundredthsSnapshot = item.ProteinPer100gHundredthsSnapshot,
                ProteinGramsHundredths = Nutrition.Protein(item.WeightGramsTenths, item.ProteinPer100gHundredthsSnapshot),
                SourceSnapshot = item.SourceSnapshot, Note = ReviewNote };
        }).ToArray();
        journal.SaveTemplateBatch(batchId.Value, batchTemplateId.Value, entries);
        BatchSaved = true; SavedAnyBatch = true; Status = $"整餐已一次保存 {entries.Length} 项；本批不能重复提交。再次记录请重新带入模板。";
    }
}
