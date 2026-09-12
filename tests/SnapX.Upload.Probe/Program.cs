using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SnapX.Core.Upload;
using SnapX.Core.Upload.Img;
using SnapX.Core.Upload.File;
using SnapX.Core.Upload.OAuth;
using Factory = SnapX.Core.Utils.Miscellaneous.HttpClientFactory;

if (args.Length == 2 && args[0] == "--live-sxcu") return LiveUploaderProbe.Run(args[1]);
return await Probe.Run();

static class Probe
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, typeof(Factory))]
    public static async Task<int> Run()
    {
        var field = typeof(Factory).GetField("_lazyClient", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = field.GetValue(null);
        using var server = new LocalServer();
        using var client = new HttpClient(new LocalTransport(server.Origin)) { Timeout = TimeSpan.FromSeconds(5) };
        field.SetValue(null, new Lazy<HttpClient>(() => client));
        int checks = 0, failures = 0;
        try
        {
            RunCase("Imgur authenticated multipart upload", () =>
            {
                server.Reply("/3/upload", request =>
                {
                    Require(request.Authorization == "Bearer synthetic-access", "Imgur bearer token was missing");
                    Require(request.Body.Contains("synthetic image bytes"), "Imgur multipart lost its payload");
                    Require(request.Body.Contains("fixture.png"), "Imgur filename was lost");
                    return (200, "{\"success\":true,\"status\":200,\"data\":{\"id\":\"fixture\",\"link\":\"https://i.imgur.com/fixture.png\",\"deletehash\":\"synthetic-delete\"}}");
                });
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes("synthetic image bytes"));
                var uploader = new Imgur(Auth()) { UploadMethod = AccountType.User, DirectLink = true };
                var result = uploader.Upload(stream, "fixture.png");
                Require(result?.URL == "https://i.imgur.com/fixture.png", "Imgur URL was not decoded");
            });
            RunCase("Imgur authenticated album listing", () =>
            {
                server.Reply("/3/account/me/albums", request => (200,"{\"success\":true,\"status\":200,\"data\":[{\"id\":\"fixture-album\",\"title\":\"Fixture\"}]}"));
                var albums = new Imgur(Auth()).GetAlbums();
                Require(albums.Count == 1 && albums[0].id == "fixture-album", "Imgur album list was not decoded");
            });
            RunCase("Imgur authenticated album images", () =>
            {
                server.Reply("/3/album/fixture-album/images", request => (200,"{\"success\":true,\"status\":200,\"data\":[{\"id\":\"fixture-image\",\"link\":\"https://i.imgur.com/fixture.png\"}]}"));
                var images = new Imgur(Auth()).GetAlbumImages("fixture-album");
                Require(images.Count == 1 && images[0].id == "fixture-image", "Imgur album images were not decoded");
            });
            RunCase("Imgur authentication failure", () =>
            {
                server.Reply("/3/upload", request => (403,"{\"success\":false,\"status\":403,\"data\":{\"error\":\"Permission denied\",\"request\":\"/3/upload\",\"method\":\"POST\"}}"));
                using var stream = new MemoryStream([1,2,3]);
                var uploader = new Imgur(Auth()) { UploadMethod = AccountType.User };
                var result = uploader.Upload(stream, "fixture.png");
                Require(result?.IsSuccess != true && uploader.Errors.Count > 0, "Imgur failure was not reported");
            });
            RunCase("Imgur expired-token refresh retries complete payload", () =>
            {
                int uploads = 0;
                server.Reply("/3/upload", request =>
                {
                    uploads++;
                    Require(request.Body.Contains("synthetic image bytes"), "Retry lost the original image bytes");
                    if (uploads == 1) return (401,"{\"success\":false,\"status\":401,\"data\":{\"error\":\"The access token provided is invalid.\"}}");
                    Require(request.Authorization == "Bearer synthetic-renewed", "Retry did not use renewed token");
                    return (200,"{\"success\":true,\"status\":200,\"data\":{\"id\":\"fixture\",\"link\":\"https://i.imgur.com/fixture.png\"}}");
                });
                server.Reply("/oauth2/token", request =>
                {
                    Require(request.Body.Contains("synthetic-refresh"), "Refresh token was missing");
                    return (200,"{\"access_token\":\"synthetic-renewed\",\"refresh_token\":\"synthetic-refresh\",\"expires_in\":3600}");
                });
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes("synthetic image bytes"));
                var uploader = new Imgur(Auth()) { UploadMethod = AccountType.User, DirectLink = true };
                Require(uploader.Upload(stream, "fixture.png")?.URL == "https://i.imgur.com/fixture.png" && uploads == 2,
                    "Refresh and retry did not complete the upload");
            });
            RunCase("Dropbox authenticated upload and share link", () =>
            {
                server.Reply("/2/files/upload", request =>
                {
                    Require(request.Authorization == "Bearer synthetic-access", "Dropbox bearer token was missing");
                    Require(request.Body == "synthetic image bytes", "Dropbox binary payload changed");
                    Require(Uri.UnescapeDataString(request.Query).Contains("fixture.png"), "Dropbox upload metadata was missing");
                    return (200,"{\"name\":\"fixture.png\",\"path_display\":\"/fixture.png\",\"id\":\"id:fixture\"}");
                });
                server.Reply("/2/sharing/create_shared_link_with_settings", request =>
                {
                    Require(request.Authorization == "Bearer synthetic-access", "Dropbox share request lost authorization");
                    Require(request.Body.Contains("requested_visibility"), "Dropbox share settings were missing");
                    return (200,"{\"url\":\"https://www.dropbox.com/scl/fi/fixture/fixture.png?dl=0\"}");
                });
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes("synthetic image bytes"));
                var uploader = new Dropbox(Auth()) { UploadPath = "/", AutoCreateShareableLink = true };
                var result = uploader.Upload(stream, "fixture.png");
                Require(result?.IsSuccess == true && result.URL?.Contains("dropbox.com/scl/fi/fixture") == true, "Dropbox share URL was not decoded");
            });
            RunCase("Dropbox unauthorized upload is not success", () =>
            {
                server.Reply("/2/files/upload", request => (401,"{\"error_summary\":\"invalid_access_token/\",\"error\":{\".tag\":\"invalid_access_token\"}}"));
                using var stream = new MemoryStream([1,2,3]);
                var uploader = new Dropbox(Auth()) { UploadPath = "/" };
                var result = uploader.Upload(stream, "fixture.png");
                Require(result?.IsSuccess != true && uploader.Errors.Count > 0, "Dropbox authorization failure was not reported");
            });
        }
        finally { field.SetValue(null, previous); }
        await server.Stop();
        Console.WriteLine($"Loopback authenticated uploader probe: {checks} passed, {failures} failed; JSON reflection disabled.");
        return failures == 0 ? 0 : 1;

        void RunCase(string name, Action test)
        {
            try { test(); checks++; Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
            finally { server.Clear(); }
        }
    }
    static OAuth2Info Auth() => new("synthetic-client", "synthetic-secret")
    { Token = new OAuth2Token { access_token = "synthetic-access", refresh_token = "synthetic-refresh", ExpireDate = DateTime.UtcNow.AddHours(1) } };
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
record Request(string Authorization, string Body, string Query);
sealed class LocalTransport(Uri origin) : DelegatingHandler(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri?.Host is not ("api.imgur.com" or "api.dropboxapi.com" or "content.dropboxapi.com"))
            throw new InvalidOperationException("Test transport blocked an unexpected destination.");
        request.RequestUri = new Uri(origin, request.RequestUri.PathAndQuery);
        request.Version = HttpVersion.Version11;
        return base.SendAsync(request, token);
    }
}
sealed class LocalServer : IDisposable
{
    readonly HttpListener listener = new();
    readonly Dictionary<string, Func<Request,(int Status,string Body)>> replies = new();
    readonly Task loop;
    public Uri Origin { get; }
    public LocalServer()
    {
        var port = new TcpListener(IPAddress.Loopback,0); port.Start(); int number=((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        Origin = new Uri($"http://127.0.0.1:{number}/"); listener.Prefixes.Add(Origin.ToString()); listener.Start(); loop=Listen();
    }
    public void Reply(string path, Func<Request,(int,string)> response) { lock(replies) replies[path]=response; }
    public void Clear() { lock(replies) replies.Clear(); }
    async Task Listen()
    {
        try
        {
            while(listener.IsListening)
            {
                var context=await listener.GetContextAsync();
                (int Status,string Body) reply;
                try
                {
                    using var reader=new StreamReader(context.Request.InputStream);
                    string body=await reader.ReadToEndAsync();
                    Func<Request,(int,string)> handler;
                    lock(replies) handler=replies[context.Request.Url!.AbsolutePath];
                    reply=handler(new Request(context.Request.Headers["Authorization"] ?? "",body,context.Request.Url!.Query));
                }
                catch(Exception ex) { reply=(500,ex.Message); }
                byte[] bytes=Encoding.UTF8.GetBytes(reply.Body); context.Response.StatusCode=reply.Status; context.Response.ContentType="application/json";
                context.Response.ContentLength64=bytes.Length; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            }
        }
        catch(HttpListenerException) { }
        catch(ObjectDisposedException) { }
    }
    public async Task Stop() { listener.Stop(); await loop; }
    public void Dispose() => listener.Close();
}
