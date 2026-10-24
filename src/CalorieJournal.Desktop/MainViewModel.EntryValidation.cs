using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalorieJournal.Desktop;

public enum EntryInputField { Date, Meal, Name, Weight, UnitEnergy, DirectEnergy }

public sealed partial class MainViewModel
{
    public event Action<EntryInputField>? EntryValidationFailed;
    public bool EntryDateInputValid { get; set; } = true;
    [ObservableProperty] private string entryValidationMessage = "";
    private void ValidateRequiredEntryFields()
    {
        EntryValidationMessage = "";
        Require(EntryDateInputValid && EntryDate != default && EntryDate.Date <= DateTime.Today, EntryInputField.Date, "请填写有效的记录日期，不能晚于今天。");
        Require(EntryMeal is not null, EntryInputField.Meal, "请选择餐次。");
        Require(!string.IsNullOrWhiteSpace(EntryName), EntryInputField.Name, "请填写食物名称。");
        if (WeightMode)
        {
            Require(!string.IsNullOrWhiteSpace(Weight), EntryInputField.Weight, "请填写可食重量。");
            Require(!string.IsNullOrWhiteSpace(UnitKcal), EntryInputField.UnitEnergy, "请填写每100克能量。");
        }
        else Require(!string.IsNullOrWhiteSpace(DirectKcal), EntryInputField.DirectEnergy, "请填写本项热量，零摄入请明确填写0。");
        void Require(bool valid, EntryInputField field, string message)
        {
            if (valid) return;
            EntryValidationMessage = message; EntryValidationFailed?.Invoke(field); throw new ValidationException(message);
        }
    }
}
