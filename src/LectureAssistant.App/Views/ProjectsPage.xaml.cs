using LectureAssistant.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LectureAssistant.App.Views;

public sealed partial class ProjectsPage : Page
{
    public ProjectsViewModel ViewModel { get; } = App.Services.GetRequiredService<ProjectsViewModel>();

    public ProjectsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private async void NewLecture_Click(object sender, RoutedEventArgs e)
    {
        if (await ViewModel.CreateFromVideoAsync() is { } id) App.MainWindow.OpenLecture(id);
    }

    private void Projects_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProjectSummary project) App.MainWindow.OpenLecture(project.Id);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProjectSummary project) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete \"{project.Title}\"?",
            Content = "Its captions and questions will be removed from this PC. Your video file, YouTube video and anything you've exported are not affected.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.DeleteAsync(project);
    }
}
