using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace TrayCer.Views;

/// <summary>
/// Shows a job preview in an <see cref="Image"/>, fetched through the hub (it may need the
/// printer's credentials) and decoded at display size. The frame stays collapsed until there's a
/// picture, so printers without previews never leave a gap.
/// </summary>
internal sealed class ThumbnailLoader(Image image, FrameworkElement frame, int decodeWidth)
{
    private CancellationTokenSource? _load;
    private string? _url;

    public async void Load(string printerId, string? url)
    {
        if (url == _url)
        {
            return;
        }

        Cancel();
        _url = url;
        if (url is null)
        {
            Clear();
            return;
        }

        CancellationTokenSource load = _load = new CancellationTokenSource();
        try
        {
            byte[]? bytes = await App.Current.Hub.GetThumbnailAsync(printerId, url, load.Token);
            if (load.IsCancellationRequested)
            {
                return;
            }

            if (bytes is null)
            {
                Clear();
                return;
            }

            BitmapImage bitmap = new() { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical };
            using InMemoryRandomAccessStream stream = new();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            if (!load.IsCancellationRequested)
            {
                image.Source = bitmap;
                frame.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or COMException or ArgumentException)
        {
            // Cancelled, or not an image the decoder understands: no preview.
            if (!load.IsCancellationRequested)
            {
                Clear();
            }
        }
    }

    /// <summary>Stops a load in flight; the next <see cref="Load"/> starts over.</summary>
    public void Cancel()
    {
        _load?.Cancel();
        _load = null;
        _url = null;
    }

    private void Clear()
    {
        image.Source = null;
        frame.Visibility = Visibility.Collapsed;
    }
}
