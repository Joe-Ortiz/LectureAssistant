using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LectureAssistant.App.Helpers;
using LectureAssistant.App.Services;
using LectureAssistant.Core.Models;
using LectureAssistant.Core.Persistence;

namespace LectureAssistant.App.ViewModels;

public sealed record ProjectSummary(string Id, string Title, string Details, string Modified)
{
    // ListView items use this as their accessible name, so screen readers announce the lecture.
    public override string ToString() => $"{Title}, {Details}";
}

public sealed partial class ProjectsViewModel(ProjectStore store) : ObservableObject
{
    public ObservableCollection<ProjectSummary> Projects { get; } = [];

    [ObservableProperty] public partial bool IsEmpty { get; set; }

    public async Task LoadAsync()
    {
        Projects.Clear();
        foreach (var p in await store.ListAsync())
        {
            var details = new List<string>();
            if (p.Duration is { } d) details.Add(TimeText.Format(d));
            details.Add(p.Captions.Count > 0 ? $"{p.Captions.Count} captions" : "no captions yet");
            details.Add(p.Questions.Count == 1 ? "1 question" : $"{p.Questions.Count} questions");
            if (p.YouTubeVideoId is null) details.Add("no YouTube link");
            Projects.Add(new ProjectSummary(p.Id, p.Title, string.Join(" · ", details), p.ModifiedAt.LocalDateTime.ToString("g")));
        }
        IsEmpty = Projects.Count == 0;
    }

    /// <summary>Asks for a video and creates a lecture for it. Returns the new project's id, or null if cancelled.</summary>
    public async Task<string?> CreateFromVideoAsync()
    {
        var path = await Pickers.OpenFileAsync(Pickers.VideoTypes);
        if (path is null) return null;

        var project = new LectureProject
        {
            Title = Path.GetFileNameWithoutExtension(path),
            SourceVideoPath = path,
            Duration = await WindowsAudioExtractor.GetDurationAsync(path),
        };
        await store.SaveAsync(project);
        return project.Id;
    }

    public async Task DeleteAsync(ProjectSummary project)
    {
        store.Delete(project.Id);
        await LoadAsync();
    }
}
