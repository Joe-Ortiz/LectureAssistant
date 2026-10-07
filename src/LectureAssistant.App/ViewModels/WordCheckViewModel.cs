using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureAssistant.Core.Captions;

namespace LectureAssistant.App.ViewModels;

/// <summary>One row of "Words to check": a word that may be misspelled, with a box for the correct spelling.</summary>
public sealed partial class WordCheckViewModel(WordToCheck word, Action<WordCheckViewModel> apply, Action<WordCheckViewModel> keep) : ObservableObject
{
    public string Word { get; } = word.Word;

    public string Detail => (word.Count == 1 ? "Appears once" : $"Appears {word.Count} times")
        + (word.Unsure ? " · the speech model wasn't sure about it" : "");

    [ObservableProperty] public partial string Replacement { get; set; } = word.Word;
    [ObservableProperty] public partial bool Remember { get; set; } = true;

    /// <summary>Replaces every occurrence with <see cref="Replacement"/>.</summary>
    [RelayCommand]
    private void Apply() => apply(this);

    /// <summary>The word is already spelled correctly.</summary>
    [RelayCommand]
    private void Keep() => keep(this);
}
