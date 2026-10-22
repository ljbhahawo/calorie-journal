using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalorieJournal.Desktop;

public sealed partial class FoodLookupViewModel : ObservableObject
{
    private readonly FoodLookupService service;
    private FoodReferenceCatalog[] catalogs = [];
    private CancellationTokenSource? updateCancellation;
    public ObservableCollection<FoodReferenceHit> Results { get; } = [];
    public string[] Providers { get; } = ["全部来源", "USDA SR Legacy", "TFDA"];
    [ObservableProperty] private string query = "";
    [ObservableProperty] private string provider = "全部来源";
    [ObservableProperty] private FoodReferenceHit? selected;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string status = "输入中文或英文关键词，点击搜索。每个来源最多显示100项，可增加关键词缩小范围。";
    public string CatalogStatus => string.Join("；", catalogs.Select(x => x.Provider == "TFDA"
        ? $"TFDA {x.Foods.Length}项 · 获取{x.RetrievedAtUtc.ToLocalTime():yyyy-MM-dd}"
        : $"{x.Provider} {x.Foods.Length}项 · {x.Version} · 获取{x.RetrievedAtUtc.ToLocalTime():yyyy-MM-dd}"));
    public string SelectedDetails => Selected?.Details ?? "选择一项，查看原名、加工状态、来源编号及原始链接。\n不同来源和状态的数值各自独立，请选择与你的食物一致的条目。";
    public bool NotBusy => !IsBusy;
    public IRelayCommand SearchCommand { get; }
    public IRelayCommand BundledCommand { get; }
    public IRelayCommand OpenSourceCommand { get; }
    public IAsyncRelayCommand UpdateCommand { get; }
    public IRelayCommand CancelUpdateCommand { get; }

    public FoodLookupViewModel(FoodLookupService service)
    {
        this.service = service;
        SearchCommand = new RelayCommand(Search, () => !IsBusy);
        BundledCommand = new RelayCommand(() => Load(true), () => !IsBusy);
        OpenSourceCommand = new RelayCommand(OpenSource, () => Selected is not null && !IsBusy);
        UpdateCommand = new AsyncRelayCommand(UpdateAsync, () => !IsBusy);
        CancelUpdateCommand = new RelayCommand(CancelUpdate, () => IsBusy);
        Load(false);
    }

    partial void OnSelectedChanged(FoodReferenceHit? value) { OnPropertyChanged(nameof(SelectedDetails)); OpenSourceCommand.NotifyCanExecuteChanged(); }
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(NotBusy)); SearchCommand.NotifyCanExecuteChanged(); BundledCommand.NotifyCanExecuteChanged();
        OpenSourceCommand.NotifyCanExecuteChanged(); UpdateCommand.NotifyCanExecuteChanged(); CancelUpdateCommand.NotifyCanExecuteChanged();
    }

    private void Load(bool bundledOnly)
    {
        try
        {
            catalogs = service.Load(bundledOnly); Results.Clear(); Selected = null;
            OnPropertyChanged(nameof(CatalogStatus));
            if (bundledOnly) Status = "本次使用随包官方资料；本机下载缓存保持不变。请输入关键词搜索。";
        }
        catch (Exception exception) when (Expected(exception)) { Status = "读取资料失败：" + exception.Message + " 可点击“使用随包资料”重新读取。"; }
    }

    private void Search()
    {
        try
        {
            Validation.Require(catalogs.Length == 2, "参考目录未加载，请先读取随包资料或联网更新。");
            var hits = FoodReferenceSearch.Search(catalogs, FoodLookupService.Simplify(Query), Provider, out var total);
            Results.Clear(); foreach (var hit in hits) Results.Add(hit); Selected = null;
            Status = total == 0 ? "没有匹配结果。可尝试别名或英文，或按包装标签手动新增食物。" : $"找到{total}项，显示{hits.Length}项。请选择加工状态一致的资料；带入后仍需核对并保存。";
        }
        catch (Exception exception) when (Expected(exception)) { Status = "查询未完成：" + exception.Message; }
    }

    private async Task UpdateAsync()
    {
        IsBusy = true; updateCancellation = new CancellationTokenSource();
        Status = "正在下载TFDA官方公开资料并校验。不会上传查询词或个人记录，可取消。";
        try
        {
            var catalog = await service.UpdateTfdaAsync(updateCancellation.Token);
            catalogs = [catalogs.SingleOrDefault(x => x.Provider == "USDA SR Legacy") ?? service.Load(true)[0], catalog];
            OnPropertyChanged(nameof(CatalogStatus));
            Results.Clear(); Selected = null;
            if (!string.IsNullOrWhiteSpace(Query)) { Search(); Status = "TFDA资料已更新。" + Status; }
            else Status = $"已更新TFDA，共{catalog.Foods.Length}项。请输入关键词搜索。";
        }
        catch (OperationCanceledException) { Status = updateCancellation.IsCancellationRequested ? "已取消更新，原资料和个人数据保持不变。" : "联网更新超时，原资料和个人数据保持不变。"; }
        catch (Exception exception) when (Expected(exception))
        {
            Status = "联网更新失败，原资料保留：" + exception.Message +
                (exception.InnerException is null ? "" : " " + exception.GetBaseException().Message);
        }
        finally { updateCancellation.Dispose(); updateCancellation = null; IsBusy = false; }
    }

    public void CancelUpdate() => updateCancellation?.Cancel();

    private void OpenSource()
    {
        if (Selected is null) return;
        try { Process.Start(new ProcessStartInfo(Selected.SourceUrl) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { Status = "无法打开浏览器：" + exception.Message; }
    }

    private static bool Expected(Exception exception) => exception is ValidationException or IOException or UnauthorizedAccessException or JsonException or HttpRequestException or ArgumentException or System.ComponentModel.Win32Exception;
}
