using System.Text.Json;
using System.Text.Json.Serialization;
using LectureAssistant.Core.Models;

namespace LectureAssistant.Core.Persistence;

/// <summary>Saves each lecture as <c>{root}/{id}/project.json</c>, next to its working files (extracted audio, exports).</summary>
public sealed class ProjectStore(string rootDirectory)
{
    private const string FileName = "project.json";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string RootDirectory { get; } = rootDirectory;

    public string GetProjectDirectory(string projectId)
    {
        var dir = Path.Combine(RootDirectory, projectId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async Task SaveAsync(LectureProject project, CancellationToken cancellationToken = default)
    {
        project.ModifiedAt = DateTimeOffset.Now;
        var path = Path.Combine(GetProjectDirectory(project.Id), FileName);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, project, JsonOptions, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<LectureProject?> LoadAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(RootDirectory, projectId, FileName);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LectureProject>(stream, JsonOptions, cancellationToken);
    }

    /// <summary>All saved projects, most recently modified first. Unreadable files are skipped.</summary>
    public async Task<IReadOnlyList<LectureProject>> ListAsync(CancellationToken cancellationToken = default)
    {
        var projects = new List<LectureProject>();
        if (!Directory.Exists(RootDirectory)) return projects;

        foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
        {
            try
            {
                if (await LoadAsync(Path.GetFileName(dir), cancellationToken) is { } p) projects.Add(p);
            }
            catch (JsonException) { }
        }
        return projects.OrderByDescending(p => p.ModifiedAt).ToList();
    }

    public void Delete(string projectId)
    {
        var dir = Path.Combine(RootDirectory, projectId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
