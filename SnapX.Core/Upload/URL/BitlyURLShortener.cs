// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using SnapX.Core.Upload.BaseServices;
using SnapX.Core.Upload.BaseUploaders;
using SnapX.Core.Upload.OAuth;
using SnapX.Core.Upload.Utils;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Miscellaneous;

namespace SnapX.Core.Upload.URL;

public sealed class BitlyURLShortenerService : URLShortenerService
{
    public override UrlShortenerType EnumValue => UrlShortenerType.BITLY;

    public override bool CheckConfig(UploadersConfig config) => OAuth2Info.CheckOAuth(config.BitlyOAuth2Info);

    public override URLShortener CreateShortener(UploadersConfig config, TaskReferenceHelper taskInfo)
    {
        config.BitlyOAuth2Info ??= new OAuth2Info(APIKeys.BitlyClientID, APIKeys.BitlyClientSecret);
        return new BitlyURLShortener(config.BitlyOAuth2Info) { Domain = config.BitlyDomain };
    }
}

[JsonSerializable(typeof(BitlyShortenRequest))]
[JsonSerializable(typeof(BitlyShortenResponse))]
internal partial class BitlyJsonContext : JsonSerializerContext;

public sealed class BitlyURLShortener : URLShortener, IOAuth2Basic
{
    private const string AccessTokenUrl = "https://api-ssl.bitly.com/oauth/access_token";
    private const string ShortenUrl = "https://api-ssl.bitly.com/v4/shorten";

    public OAuth2Info AuthInfo { get; }
    public string Domain { get; set; } = "bit.ly";

    public BitlyURLShortener(OAuth2Info oauth)
    {
        AuthInfo = oauth ?? throw new ArgumentNullException(nameof(oauth));
    }

    public string? GetAuthorizationURL()
    {
        if (string.IsNullOrWhiteSpace(AuthInfo.Client_ID)) return null;

        return URLHelpers.CreateQueryString("https://bitly.com/oauth/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = AuthInfo.Client_ID,
            ["redirect_uri"] = Links.Callback
        });
    }

    public bool GetAccessToken(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(AuthInfo.Client_ID) ||
            string.IsNullOrWhiteSpace(AuthInfo.Client_Secret))
        {
            Errors.Add("Bitly OAuth requires a client ID, client secret, and authorization code.");
            return false;
        }

        var response = SendRequestURLEncoded(HttpMethod.Post, AccessTokenUrl, new Dictionary<string, string?>
        {
            ["client_id"] = AuthInfo.Client_ID,
            ["client_secret"] = AuthInfo.Client_Secret,
            ["code"] = code,
            ["redirect_uri"] = Links.Callback
        });

        var token = string.IsNullOrWhiteSpace(response)
            ? null
            : HttpUtility.ParseQueryString(response)["access_token"];

        if (string.IsNullOrWhiteSpace(token))
        {
            Errors.Add("Bitly did not return an access token.");
            return false;
        }

        AuthInfo.Token = new OAuth2Token { access_token = token };
        return true;
    }

    public override UploadResult ShortenURL(string? url)
    {
        var result = new UploadResult { URL = url };
        if (string.IsNullOrWhiteSpace(url)) return result;

        if (!UploaderResponseValidator.TryGetHttpUri(url, out _))
        {
            Errors.Add("Bitly requires an absolute HTTP or HTTPS URL.");
            return result;
        }

        if (!OAuth2Info.CheckOAuth(AuthInfo))
        {
            Errors.Add("Bitly authorization is missing or invalid.");
            return result;
        }

        var request = new BitlyShortenRequest
        {
            LongUrl = url,
            Domain = string.IsNullOrWhiteSpace(Domain) ? "bit.ly" : Domain.Trim()
        };
        var json = JsonSerializer.Serialize(request, BitlyJsonContext.Default.BitlyShortenRequest);
        var headers = new NameValueCollection { ["Authorization"] = "Bearer " + AuthInfo.Token.access_token };

        result.Response = SendRequest(HttpMethod.Post, ShortenUrl, json, RequestHelpers.ContentTypeJSON, headers: headers);
        result.ResponseInfo = LastResponseInfo;
        result.IsSuccess = LastResponseInfo?.IsSuccess == true;

        if (string.IsNullOrWhiteSpace(result.Response))
        {
            result.IsSuccess = false;
            Errors.Add("Bitly returned an empty response.");
            return result;
        }

        try
        {
            var response = JsonSerializer.Deserialize(result.Response, BitlyJsonContext.Default.BitlyShortenResponse);
            if (result.IsSuccess && UploaderResponseValidator.TryGetHttpUri(response?.Link, out var uri, "bit.ly", request.Domain))
            {
                result.ShortenedURL = uri!.AbsoluteUri;
            }
            else
            {
                result.IsSuccess = false;
                Errors.Add("Bitly did not return a valid bit.ly URL.");
            }
        }
        catch (JsonException ex)
        {
            result.IsSuccess = false;
            Errors.Add("Bitly returned invalid JSON: " + ex.Message);
        }

        return result;
    }
}

public sealed class BitlyShortenRequest
{
    [JsonPropertyName("long_url")]
    public string? LongUrl { get; set; }

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = "bit.ly";
}

public sealed class BitlyShortenResponse
{
    [JsonPropertyName("link")]
    public string? Link { get; set; }
}
