
// SPDX-License-Identifier: GPL-3.0-or-later


using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SnapX.Core.Utils.Miscellaneous;

namespace SnapX.Core.Utils;

public static class WebHelpers
{
    private const int MaximumRedirects = 5;
    private const int MaximumImageDownloadBytes = 64 * 1024 * 1024;
    private const int MaximumDataUrlBytes = 64 * 1024 * 1024;

    public static async Task DownloadFileAsync(string url, string? filePath)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(filePath))
        {
            return;
        }

        FileHelpers.CreateDirectoryFromFilePath(filePath);

        using var responseMessage = await SendSafeExternalRequestAsync(HttpMethod.Get, url);

        if (!responseMessage.IsSuccessStatusCode)
        {
            DebugHelper.Logger.Error("{url}: {responseMessage.ReasonPhrase}", url, responseMessage);
            return;
        }

        await using var responseStream = await responseMessage.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);

        await responseStream.CopyToAsync(fileStream);
    }

    public static async Task<Image> DataURLToImage(string? url)
    {
        // Ensure the URL is valid and starts with "data:"
        if (url == null || !url.ToString().StartsWith("data:"))
        {
            throw new ArgumentException("Invalid data URL.");
        }

        var dataUrl = url;
        var regex = new Regex(@"^data:image\/(?<type>.*?);base64,(?<data>.+)$");
        var match = regex.Match(dataUrl);

        if (!match.Success)
        {
            throw new ArgumentException("Invalid data URL format.");
        }

        var base64Data = match.Groups["data"].Value;
        if (base64Data.Length > MaximumDataUrlBytes * 4L / 3L + 4)
        {
            throw new InvalidDataException("The data URL exceeds the supported image size.");
        }

        byte[] imageBytes = Convert.FromBase64String(base64Data);

        using var ms = new MemoryStream(imageBytes);
        var image = await Image.LoadAsync(ms);
        return image;
    }

    public static async Task<string> DownloadStringAsync(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        using var responseMessage = await SendSafeExternalRequestAsync(HttpMethod.Get, url);
        if (!responseMessage.IsSuccessStatusCode)
        {
            DebugHelper.Logger.Error("{url}: {responseMessage.ReasonPhrase}", url, responseMessage);
            return null;
        }

        return await responseMessage.Content.ReadAsStringAsync();
    }



    public static async Task<string?> GetFileNameFromWebServerAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        using var responseMessage = await SendSafeExternalRequestAsync(HttpMethod.Head, url);

        return responseMessage.Content.Headers.ContentDisposition?.FileName;
    }


    public static async Task<Image?> DownloadImageAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {

            using var responseMessage = await SendSafeExternalRequestAsync(HttpMethod.Get, url);

            if (!responseMessage.IsSuccessStatusCode)
            {
                DebugHelper.Logger.Error("{url}: {responseMessage.ReasonPhrase}", url, responseMessage);
                return null;
            }


            var mediaType = responseMessage.Content.Headers.ContentType?.MediaType;
            if (mediaType == null)
            {
                DebugHelper.Logger.Error("{url}: mediaType is null.", url);
                return null;
            }

            if (!MimeTypesPlus.IsImageMimeType(mediaType))
            {
                DebugHelper.Logger.Error("{url}: mediaType/Mimetype is not a known image type.", url);
                return null;
            }

            var data = await ReadContentWithLimitAsync(responseMessage.Content, MaximumImageDownloadBytes);

            using var memoryStream = new MemoryStream(data);
            return await Image.LoadAsync(memoryStream);
        }
        catch (Exception ex)
        {
            DebugHelper.Logger.Error("{url}: {message}", url, ex.Message);
            DebugHelper.WriteException(ex);
            return null;
        }
    }

    public static bool IsSuccessStatusCode(HttpStatusCode statusCode)
    {
        var statusCodeNum = (int)statusCode;
        return statusCodeNum >= 200 && statusCodeNum <= 299;
    }

    public static int GetRandomUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<HttpResponseMessage> SendSafeExternalRequestAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target))
        {
            throw new ArgumentException("The external URL is invalid.", nameof(url));
        }

        var client = HttpClientFactory.GetSafeExternalClient();
        for (var redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            if (!await URLHelpers.IsSafePublicHttpUrlAsync(target.AbsoluteUri, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The external URL does not resolve to a public Internet address.");
            }

            var request = new HttpRequestMessage(method, target);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            request.Dispose();

            if (location is null)
            {
                throw new HttpRequestException("The external server returned a redirect without a Location header.");
            }

            target = location.IsAbsoluteUri ? location : new Uri(target, location);
        }

        throw new HttpRequestException($"The external URL exceeded the redirect limit of {MaximumRedirects}.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => (int)statusCode is >= 300 and < 400;

    private static async Task<byte[]> ReadContentWithLimitAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
        {
            throw new InvalidDataException($"The response exceeds the maximum allowed size of {maximumBytes} bytes.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream();
        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            totalRead += read;
            if (totalRead > maximumBytes)
            {
                throw new InvalidDataException($"The response exceeds the maximum allowed size of {maximumBytes} bytes.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }
}
