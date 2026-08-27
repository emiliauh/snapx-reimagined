// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using SnapX.Core.Upload.BaseServices;
using SnapX.Core.Upload.BaseUploaders;
using SnapX.Core.Upload.Utils;

namespace SnapX.Core.Upload.Text;

public sealed class UpasteTextUploaderService : TextUploaderService
{
    public override TextDestination EnumValue => TextDestination.Upaste;

    public override bool CheckConfig(UploadersConfig config) => !string.IsNullOrWhiteSpace(config.UpasteUserKey);

    public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo) =>
        new Upaste(config.UpasteUserKey) { IsPublic = config.UpasteIsPublic };
}

[JsonSerializable(typeof(UpasteResponse))]
internal partial class UpasteJsonContext : JsonSerializerContext;

public sealed class Upaste(string userKey) : TextUploader
{
    private const string ApiUrl = "https://upaste.me/api";

    public string UserKey { get; } = userKey ?? "";
    public bool IsPublic { get; set; }

    public override UploadResult UploadText(string? text, string? fileName)
    {
        var result = new UploadResult();
        if (string.IsNullOrEmpty(text)) return result;

        if (string.IsNullOrWhiteSpace(UserKey))
        {
            Errors.Add("uPaste requires an API key.");
            return result;
        }

        var arguments = new Dictionary<string, string?>
        {
            ["api_key"] = UserKey,
            ["paste"] = text,
            ["privacy"] = IsPublic ? "0" : "1",
            ["expire"] = "0",
            ["json"] = "true"
        };

        result.Response = SendRequestMultiPart(ApiUrl, arguments);
        result.ResponseInfo = LastResponseInfo;
        result.IsSuccess = LastResponseInfo?.IsSuccess == true;

        if (string.IsNullOrWhiteSpace(result.Response))
        {
            result.IsSuccess = false;
            Errors.Add("uPaste returned an empty response.");
            return result;
        }

        UpasteResponse? response;
        try
        {
            response = JsonSerializer.Deserialize(result.Response, UpasteJsonContext.Default.UpasteResponse);
        }
        catch (JsonException ex)
        {
            result.IsSuccess = false;
            Errors.Add("uPaste returned invalid JSON: " + ex.Message);
            return result;
        }

        if (result.IsSuccess &&
            response?.Status?.Equals("success", StringComparison.OrdinalIgnoreCase) == true &&
            UploaderResponseValidator.TryGetHttpUri(response.Paste?.Link, out var uri, "upaste.me"))
        {
            result.URL = uri!.AbsoluteUri;
            return result;
        }

        result.IsSuccess = false;
        Errors.Add(!string.IsNullOrWhiteSpace(response?.Error) ? response.Error : "uPaste did not return a valid paste URL.");
        return result;
    }
}

public sealed class UpastePaste
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("raw")]
    public string? Raw { get; set; }

    [JsonPropertyName("download")]
    public string? Download { get; set; }
}

public sealed class UpasteResponse
{
    [JsonPropertyName("paste")]
    public UpastePaste? Paste { get; set; }

    [JsonPropertyName("errorcode")]
    public int ErrorCode { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}
