using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ovi.Screenshot.Views;

namespace Ovi.Screenshot.Services;

internal sealed class UserDialogs(SettingsWindow settings)
{
    internal async Task<bool> ConfirmDiscardRecordingAsync(CancellationToken cancellation)
    {
        bool wasVisible = settings.AppWindow.IsVisible;
        var root = (FrameworkElement)settings.Content;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
        root.Loaded += OnLoaded;
        try
        {
            settings.Activate();
            if (root.XamlRoot is null) await loaded.Task.WaitAsync(cancellation);
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = "Discard recording?", Content = "This recording has not been saved.",
                PrimaryButtonText = "Discard", CloseButtonText = "Keep", DefaultButton = ContentDialogButton.Close
            };
            using var cancel = cancellation.Register(() => settings.DispatcherQueue.TryEnqueue(dialog.Hide));
            return await dialog.ShowAsync().AsTask(cancellation) == ContentDialogResult.Primary;
        }
        finally
        {
            root.Loaded -= OnLoaded;
            if (!wasVisible) settings.AppWindow.Hide();
        }
    }
}
