using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureAssistant.App.Helpers;
using LectureAssistant.Core.Models;

namespace LectureAssistant.App.ViewModels;

public sealed partial class CaptionViewModel : ObservableObject
{
    private readonly Action<TimeSpan> _seek;
    private readonly List<string>? _uncertainWords;

    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public string StartText => TimeText.FormatPrecise(Start);

    [ObservableProperty] public partial string Text { get; set; }

    public CaptionViewModel(CaptionSegment model, Action<TimeSpan> seek, Action changed)
    {
        _seek = seek;
        Start = model.Start;
        End = model.End;
        Text = model.Text;
        _uncertainWords = model.UncertainWords;
        PropertyChanged += (_, _) => changed();
    }

    [RelayCommand]
    private void Jump() => _seek(Start);

    public CaptionSegment ToModel() => new(Start, End, Text.Trim()) { UncertainWords = _uncertainWords };
}
