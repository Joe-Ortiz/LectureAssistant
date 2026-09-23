using LectureAssistant.App.Services;
using LectureAssistant.App.ViewModels;
using LectureAssistant.Core;
using LectureAssistant.Core.Persistence;
using LectureAssistant.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace LectureAssistant.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        Services = ConfigureServices();
    }

    private static void LogCrash(Exception? exception)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.DataRoot, "crash.log"), $"[{DateTimeOffset.Now:u}] {exception}\n\n");
        }
        catch (IOException) { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<SettingsService>();
        services.AddSingleton<SecretStore>();
        services.AddSingleton(_ => new ProjectStore(AppPaths.Projects));
        services.AddSingleton(_ => new WhisperModelManager(AppPaths.WhisperModels));
        services.AddSingleton<IAudioExtractor, WindowsAudioExtractor>();
        services.AddSingleton<ITranscriber>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            var models = sp.GetRequiredService<WhisperModelManager>();
            return new WhisperTranscriber(() => models.GetPath(WhisperModels.Find(settings.Current.WhisperModelId)));
        });
        services.AddSingleton<QuestionGeneratorFactory>();
        services.AddSingleton<ExporterCatalog>();

        services.AddTransient<ProjectsViewModel>();
        services.AddTransient<LectureViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }
}
