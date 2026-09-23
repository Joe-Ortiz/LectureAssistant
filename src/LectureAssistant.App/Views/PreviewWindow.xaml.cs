using LectureAssistant.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace LectureAssistant.App.Views;

/// <summary>
/// Shows the lecture exactly as students will get it, in the built-in Edge (WebView2) browser, with a page
/// standing in for the LMS so the instructor can see what the gradebook would record.
/// </summary>
public sealed partial class PreviewWindow : Window
{
    private static readonly List<PreviewWindow> Open = [];

    public PreviewWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));
        Open.Add(this);
        Closed += (_, _) => Open.Remove(this);
    }

    /// <summary>Closes every preview (when the main window closes, so the app doesn't linger).</summary>
    public static void CloseAll()
    {
        foreach (var window in Open.ToList()) window.Close();
    }

    public async Task ShowAsync(PreviewRequest request)
    {
        Title = $"Student preview: {request.Title}";
        Activate();

        try
        {
            await Web.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            ShowError("The preview uses the Microsoft Edge WebView2 Runtime, which couldn't start on this PC. " +
                      "Installing or repairing it from microsoft.com/edge/webview2 usually fixes this.\n\n" + ex.Message);
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping(StudentPreview.PreviewHost, request.Folder, CoreWebView2HostResourceAccessKind.Allow);
        if (request.VideoFolder is not null)
            core.SetVirtualHostNameToFolderMapping(StudentPreview.VideoHost, request.VideoFolder, CoreWebView2HostResourceAccessKind.Allow);

        // Links such as "Watch on YouTube" open in the instructor's normal browser.
        core.NewWindowRequested += async (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                await Windows.System.Launcher.LaunchUriAsync(uri);
        };
        core.NavigationCompleted += (_, _) => LoadingPanel.Visibility = Visibility.Collapsed;

        var query = request.VideoFolder is not null ? "?local=1" : "";
        core.Navigate($"https://{StudentPreview.PreviewHost}/preview.html{query}");
    }

    private void ShowError(string message)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }
}
