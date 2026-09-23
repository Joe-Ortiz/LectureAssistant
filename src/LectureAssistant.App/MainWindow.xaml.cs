using LectureAssistant.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LectureAssistant.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));

        ContentFrame.Navigate(typeof(ProjectsPage));
        Nav.SelectedItem = LecturesItem;
        Closed += async (_, _) =>
        {
            PreviewWindow.CloseAll();
            if (ContentFrame.Content is LecturePage page) await page.ViewModel.SaveIfDirtyAsync();
        };
    }

    public void OpenLecture(string projectId) => ContentFrame.Navigate(typeof(LecturePage), projectId);

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var target = args.IsSettingsInvoked ? typeof(SettingsPage) : typeof(ProjectsPage);
        if (ContentFrame.CurrentSourcePageType != target) ContentFrame.Navigate(target);
    }

    private void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        Nav.SelectedItem = e.SourcePageType == typeof(SettingsPage) ? Nav.SettingsItem : LecturesItem;
    }
}
