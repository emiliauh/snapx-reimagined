// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnapX.Core.Upload.BaseServices;
using SnapX.Core.Upload.BaseUploaders;
using SnapX.Core.Upload.Utils;

namespace SnapX.Core.Upload.URL;

public sealed class TurlURLShortenerService : URLShortenerService
{
    public override UrlShortenerType EnumValue => UrlShortenerType.TURL;

    public override bool CheckConfig(UploadersConfig config) => !string.IsNullOrWhiteSpace(config.TurlApiKey);

    public override URLShortener CreateShortener(UploadersConfig config, TaskReferenceHelper taskInfo) =>
        new TurlURLShortener(config.TurlApiKey);
}

[JsonSerializable(typeof(TurlCreateRequest))]
[JsonSerializable(typeof(TurlCreateResponse))]
internal partial class TurlJsonContext : JsonSerializerContext;

public sealed class TurlURLShortener(string apiKey) : URLShortener
{
    private const string ApiUrl = "https://turl.ca/api/v1/links";

    public string ApiKey { get; } = apiKey ?? "";

    public override UploadResult ShortenURL(string? url)
    {
        var result = new UploadResult { URL = url };
        if (string.IsNullOrWhiteSpace(url)) return result;

        if (!UploaderResponseValidator.TryGetHttpUri(url, out _))
        {
            Errors.Add("turl.ca requires an absolute HTTP or HTTPS URL.");
            return result;
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            Errors.Add("turl.ca requires an audience-bound API key with the link:create scope.");
            return result;
        }

        var body = JsonSerializer.Serialize(
            new TurlCreateRequest { Url = url },
            TurlJsonContext.Default.TurlCreateRequest);
        var headers = new NameValueCollection { ["Authorization"] = "Bearer " + ApiKey };

        result.Response = SendRequest(HttpMethod.Post, ApiUrl, body, RequestHelpers.ContentTypeJSON, headers: headers);
        result.ResponseInfo = LastResponseInfo;
        result.IsSuccess = LastResponseInfo?.IsSuccess == true;

        if (string.IsNullOrWhiteSpace(result.Response))
        {
            result.IsSuccess = false;
            Errors.Add("turl.ca returned an empty response.");
            return result;
        }

        try
        {
            var response = JsonSerializer.Deserialize(result.Response, TurlJsonContext.Default.TurlCreateResponse);
            if (result.IsSuccess &&
                UploaderResponseValidator.TryGetHttpUri(response?.ShortUrl, out var uri, "turl.ca"))
            {
                result.ShortenedURL = uri!.AbsoluteUri;
            }
            else
            {
                result.IsSuccess = false;
                Errors.Add(!string.IsNullOrWhiteSpace(response?.Error)
                    ? "turl.ca rejected the URL: " + response.Error
                    : "turl.ca did not return a valid short URL.");
            }
        }
        catch (JsonException ex)
        {
            result.IsSuccess = false;
            Errors.Add("turl.ca returned invalid JSON: " + ex.Message);
        }

        return result;
    }
}

public sealed class TurlCreateRequest
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class TurlCreateResponse
{
    [JsonPropertyName("short_url")]
    public string? ShortUrl { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
