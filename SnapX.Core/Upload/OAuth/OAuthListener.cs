
// SPDX-License-Identifier: GPL-3.0-or-later


using System.Net;
using System.Reflection;
using System.Text;
using SnapX.Core.Utils;

namespace SnapX.Core.Upload.OAuth;

public class OAuthListener : IDisposable
{
    public IOAuth2Loopback OAuth { get; private set; }

    private HttpListener? listener;

    public OAuthListener(IOAuth2Loopback oauth)
    {
        OAuth = oauth;
    }

    public void Dispose()
    {
        if (listener != null)
        {
            listener.Close();
            listener = null;
        }
    }

    public async Task<bool> ConnectAsync()
    {
        Dispose();

        try
        {
            var ip = IPAddress.Loopback;
            var port = WebHelpers.GetRandomUnusedPort();
            var redirectURI = $"http://{ip}:{port}/";
            var state = Helpers.GetRandomAlphanumeric(32);

            listener = new HttpListener();
            listener.Prefixes.Add(redirectURI);
            // Bind before opening the browser. This prevents another local process
            // from claiming the callback port while the user is authenticating.
            listener.Start();

            OAuth.RedirectURI = redirectURI;
            OAuth.State = state;
            var url = OAuth.GetAuthorizationURL();

            if (string.IsNullOrEmpty(url))
            {
                DebugHelper.WriteLine("Authorization URL is empty.");
                return false;
            }

            URLHelpers.OpenURL(url);
            DebugHelper.WriteLine("Authorization URL is opened: " + url);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            while (!timeout.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                var queryCode = context.Request.QueryString.Get("code");
                var queryState = context.Request.QueryString.Get("state");
                var isValidCallback = queryState == state && !string.IsNullOrEmpty(queryCode);
                var status = isValidCallback
                    ? "Authorization completed successfully."
                    : queryState != state
                        ? "Invalid state parameter."
                        : "Authorization did not succeed.";

                using var response = context.Response;
                await WriteCallbackResponseAsync(response, status);

                if (isValidCallback)
                {
                    return await Task.Run(() => OAuth.GetAccessToken(queryCode));
                }
            }
        }
        catch (OperationCanceledException)
        {
            DebugHelper.WriteLine("OAuth callback timed out.");
        }
        catch (ObjectDisposedException)
        {
            // Listener is DISPOSED.
        }
        finally
        {
            Dispose();
        }

        return false;
    }

    private static async Task WriteCallbackResponseAsync(HttpListenerResponse response, string status)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream("SnapX.Core.Resources.OAuthCallbackPage.html");
        if (stream == null || stream.Length == 0) return;
        using var reader = new StreamReader(stream);
        var responseText = reader.ReadToEnd().Replace("{0}", status);
        var buffer = Encoding.UTF8.GetBytes(responseText);

        response.ContentLength64 = buffer.Length;
        response.KeepAlive = false;

        await using var responseOutput = response.OutputStream;
        await responseOutput.WriteAsync(buffer);
        await responseOutput.FlushAsync();
    }
}
