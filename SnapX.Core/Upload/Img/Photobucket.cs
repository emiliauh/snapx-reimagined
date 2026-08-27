// SPDX-License-Identifier: GPL-3.0-or-later

using System.Xml.Linq;
using SnapX.Core.Upload.BaseServices;
using SnapX.Core.Upload.BaseUploaders;
using SnapX.Core.Upload.OAuth;
using SnapX.Core.Upload.Utils;

namespace SnapX.Core.Upload.Img;

public sealed class PhotobucketImageUploaderService : ImageUploaderService
{
    public override ImageDestination EnumValue => ImageDestination.Photobucket;

    public override bool CheckConfig(UploadersConfig config) =>
        config.PhotobucketAccountInfo?.HasValidActiveAlbum == true &&
        OAuthInfo.CheckOAuth(config.PhotobucketOAuthInfo);

    public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo) =>
        new Photobucket(
            config.PhotobucketOAuthInfo ?? new OAuthInfo(APIKeys.PhotobucketConsumerKey, APIKeys.PhotobucketConsumerSecret),
            config.PhotobucketAccountInfo ?? new PhotobucketAccountInfo());
}

public sealed class Photobucket : ImageUploader, IOAuth
{
    // These are the endpoints still used by the official ShareX compatibility adapter.
    // Photobucket may retire them; every returned host and media URL is validated.
    private const string RequestTokenUrl = "http://api.photobucket.com/login/request";
    private const string AuthorizeUrl = "http://photobucket.com/apilogin/login";
    private const string AccessTokenUrl = "http://api.photobucket.com/login/access";
    private const string UploadUrl = "http://api.photobucket.com/album/!/upload";

    public OAuthInfo AuthInfo { get; set; }
    public PhotobucketAccountInfo AccountInfo { get; set; }

    public Photobucket(OAuthInfo oauth) : this(oauth, new PhotobucketAccountInfo())
    {
    }

    public Photobucket(OAuthInfo oauth, PhotobucketAccountInfo accountInfo)
    {
        AuthInfo = oauth ?? throw new ArgumentNullException(nameof(oauth));
        AccountInfo = accountInfo ?? throw new ArgumentNullException(nameof(accountInfo));
    }

    public string? GetAuthorizationURL() =>
        GetAuthorizationURL(RequestTokenUrl, AuthorizeUrl, AuthInfo, httpMethod: HttpMethod.Post);

    public bool GetAccessToken(string? verificationCode = null)
    {
        AuthInfo.AuthVerifier = verificationCode;
        var values = GetAccessTokenEx(AccessTokenUrl, AuthInfo, HttpMethod.Post);
        var subdomain = values?["subdomain"];

        if (!PhotobucketAccountInfo.TryGetSubdomainUri(subdomain, out var uri))
        {
            Errors.Add("Photobucket returned an invalid account API host.");
            return false;
        }

        AccountInfo.Subdomain = uri!.GetLeftPart(UriPartial.Authority);
        AccountInfo.AlbumID = values?["username"] ?? "";
        return true;
    }

    public override UploadResult Upload(Stream stream, string? fileName)
    {
        var result = new UploadResult();
        if (!AccountInfo.HasValidActiveAlbum)
        {
            Errors.Add("Photobucket requires a valid account and active album.");
            return result;
        }

        var arguments = new Dictionary<string, string?>
        {
            ["id"] = AccountInfo.ActiveAlbumPath,
            ["type"] = "image"
        };

        string? signedUrl;
        try
        {
            signedUrl = OAuthManager.GenerateQuery(UploadUrl, arguments, HttpMethod.Post, AuthInfo);
        }
        catch (Exception ex)
        {
            Errors.Add("Unable to sign the Photobucket request: " + ex.Message);
            return result;
        }

        if (!TryUseAccountHost(signedUrl, out var accountUrl))
        {
            Errors.Add("Photobucket account host is invalid.");
            return result;
        }

        result = SendRequestFile(accountUrl, stream, fileName, "uploadfile");
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Response)) return result;

        try
        {
            var content = XDocument.Parse(result.Response)
                .Descendants("content")
                .FirstOrDefault();

            var mediaUrl = content?.Element("url")?.Value;
            var thumbnailUrl = content?.Element("thumb")?.Value;

            if (!UploaderResponseValidator.TryGetHttpUri(mediaUrl, out var mediaUri, "photobucket.com"))
            {
                result.IsSuccess = false;
                Errors.Add("Photobucket returned a successful response without a valid media URL.");
                return result;
            }

            result.URL = mediaUri!.AbsoluteUri;
            if (UploaderResponseValidator.TryGetHttpUri(thumbnailUrl, out var thumbnailUri, "photobucket.com"))
            {
                result.ThumbnailURL = thumbnailUri!.AbsoluteUri;
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            result.IsSuccess = false;
            Errors.Add("Photobucket returned invalid XML: " + ex.Message);
        }

        return result;
    }

    private bool TryUseAccountHost(string? signedUrl, out string? accountUrl)
    {
        accountUrl = null;
        if (!Uri.TryCreate(signedUrl, UriKind.Absolute, out var signedUri) ||
            !PhotobucketAccountInfo.TryGetSubdomainUri(AccountInfo.Subdomain, out var accountUri))
        {
            return false;
        }

        accountUrl = new UriBuilder(signedUri)
        {
            Scheme = accountUri!.Scheme,
            Host = accountUri.Host,
            Port = accountUri.IsDefaultPort ? -1 : accountUri.Port
        }.Uri.AbsoluteUri;
        return true;
    }
}

public sealed class PhotobucketAccountInfo
{
    public string Subdomain { get; set; } = "";
    public string AlbumID { get; set; } = "";
    public List<string> AlbumList { get; set; } = [];
    public int ActiveAlbumID { get; set; }

    public bool HasValidActiveAlbum =>
        TryGetSubdomainUri(Subdomain, out _) &&
        ActiveAlbumID >= 0 && ActiveAlbumID < AlbumList.Count &&
        !string.IsNullOrWhiteSpace(AlbumList[ActiveAlbumID]);

    public string ActiveAlbumPath => HasValidActiveAlbum ? AlbumList[ActiveAlbumID] : "";

    internal static bool TryGetSubdomainUri(string? value, out Uri? uri)
    {
        var candidate = value?.Trim();
        if (!string.IsNullOrEmpty(candidate) && !candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "http://" + candidate;
        }

        return UploaderResponseValidator.TryGetHttpUri(candidate, out uri, "photobucket.com");
    }
}
