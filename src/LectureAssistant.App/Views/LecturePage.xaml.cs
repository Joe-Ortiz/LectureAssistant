using System.Text;
using LectureAssistant.App.Services;
using LectureAssistant.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace LectureAssistant.App.Views;

public sealed partial class LecturePage : Page
{
    public LectureViewModel ViewModel { get; } = App.Services.GetRequiredService<LectureViewModel>();

    private DispatcherQueueTimer? _autosave;

    public LecturePage()
    {
        InitializeComponent();
        ViewModel.SeekRequested += Seek;
        ViewModel.CaptionsReplaced += ReloadPreview;
        ViewModel.ConfirmAsync = ConfirmAsync;
        ViewModel.PreviewReady += OpenPreviewAsync;
    }

    private PreviewWindow? _preview;

    /// <summary>One preview per lecture page: re-previewing replaces the old window so it reflects the latest edits.</summary>
    private async Task OpenPreviewAsync(PreviewRequest request)
    {
        Player.MediaPlayer?.Pause();
        _preview?.Close();
        _preview = new PreviewWindow();
        _preview.Closed += (sender, _) => { if (ReferenceEquals(sender, _preview)) _preview = null; };
        await _preview.ShowAsync(request);
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await ViewModel.LoadAsync((string)e.Parameter);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = ex.Message;
            return;
        }
        ReloadPreview();

        _autosave = DispatcherQueue.CreateTimer();
        _autosave.Interval = TimeSpan.FromSeconds(5);
        _autosave.Tick += async (_, _) => await ViewModel.SaveIfDirtyAsync();
        _autosave.Start();
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _autosave?.Stop();
        _preview?.Close();
        Player.MediaPlayer?.Pause();
        Player.SetMediaPlayer(null);
        ViewModel.CancelOperationCommand.Execute(null);
        await ViewModel.SaveIfDirtyAsync();
    }

    private TimeSpan CurrentPosition => Player.MediaPlayer?.PlaybackSession.Position ?? TimeSpan.Zero;

    /// <summary>(Re)loads the video with the current captions as a caption track, keeping the playback position.</summary>
    private void ReloadPreview()
    {
        var path = ViewModel.VideoPath;
        bool available = path is not null && File.Exists(path);
        MissingVideoText.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        if (!available) return;

        var position = CurrentPosition;
        var source = MediaSource.CreateFromUri(new Uri(path!));

        if (ViewModel.HasCaptions)
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(Encoding.UTF8.GetBytes(ViewModel.CaptionsAsWebVtt()));
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            source.ExternalTimedTextSources.Add(TimedTextSource.CreateFromStream(stream));
        }

        var item = new MediaPlaybackItem(source);
        item.TimedMetadataTracksChanged += (sender, args) =>
        {
            if (args.CollectionChange == Windows.Foundation.Collections.CollectionChange.ItemInserted)
                sender.TimedMetadataTracks.SetPresentationMode(args.Index, TimedMetadataTrackPresentationMode.PlatformPresented);
        };

        Player.Source = item;
        if (position > TimeSpan.Zero && Player.MediaPlayer is { } player) player.PlaybackSession.Position = position;
    }

    private void Seek(TimeSpan time)
    {
        if (Player.MediaPlayer is not { } player) return;
        player.PlaybackSession.Position = time;
        player.Play();
    }

    private void AddQuestionHere_Click(object sender, RoutedEventArgs e)
    {
        Player.MediaPlayer?.Pause();
        ViewModel.AddQuestionAt(TimeSpan.FromSeconds(Math.Round(CurrentPosition.TotalSeconds)));
        Steps.SelectedItem = QuestionsStep;
    }

    private void RefreshPreview_Click(object sender, RoutedEventArgs e) => ReloadPreview();

    private void Regenerate_Click(object sender, RoutedEventArgs e) => RegenerateFlyout.Hide();

    private void Steps_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        CaptionsPanel.Visibility = sender.SelectedItem == CaptionsStep ? Visibility.Visible : Visibility.Collapsed;
        QuestionsPanel.Visibility = sender.SelectedItem == QuestionsStep ? Visibility.Visible : Visibility.Collapsed;
        PublishPanel.Visibility = sender.SelectedItem == PublishStep ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OpenYouTubeStudio_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://studio.youtube.com/"));

    private void ErrorBar_Closed(InfoBar sender, object args) => ViewModel.ErrorMessage = null;

    private void SuccessBar_Closed(InfoBar sender, object args) => ViewModel.SuccessMessage = null;
}
