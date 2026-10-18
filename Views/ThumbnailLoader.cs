using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace TrayCer.Views;

/// <summary>
/// Shows a job preview in a <see cref="BuildPreview"/>, fetched through the hub (it may need the
/// printer's credentials) and decoded at display size. The frame stays collapsed until there's a
/// picture, so printers without previews never leave a gap.
/// </summary>
internal sealed class ThumbnailLoader(BuildPreview image, FrameworkElement frame, int decodeWidth)
{
    private CancellationTokenSource? _load;
    private string? _url;

    public async void Load(string printerId, string? url)
    {
        if (url == _url)
        {
            return;
        }

        _load?.Cancel();
        _load = null;
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
        finally
        {
            if (_load == load)
            {
                _load = null;
            }
        }
    }

    /// <summary>
    /// Stops a load in flight, so the next <see cref="Load"/> starts over. A picture that already
    /// finished loading stays, and so does its URL, so the next Load still notices if the printer
    /// has dropped the job in the meantime.
    /// </summary>
    public void Cancel()
    {
        if (_load is null)
        {
            return;
        }

        _load.Cancel();
        _load = null;
        _url = null;
        Clear(); // Start over from a blank frame, not from an older job's picture.
    }

    private void Clear()
    {
        image.Source = null;
        frame.Visibility = Visibility.Collapsed;
    }
}
