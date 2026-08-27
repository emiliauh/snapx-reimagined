// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using SnapX.Core.Upload.BaseServices;
using SnapX.Core.Upload.BaseUploaders;
using SnapX.Core.Upload.Utils;
using SnapX.Core.Utils;

namespace SnapX.Core.Upload.File;

public sealed class LambdaFileUploaderService : FileUploaderService
{
    public override FileDestination EnumValue => FileDestination.Lambda;

    public override bool CheckConfig(UploadersConfig config) =>
        config.LambdaSettings is not null && !string.IsNullOrWhiteSpace(config.LambdaSettings.UserAPIKey);

    public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo)
    {
        config.LambdaSettings ??= new LambdaSettings();

        if (config.LambdaSettings.UploadURL.Equals("https://λ.pw/", StringComparison.OrdinalIgnoreCase))
        {
            config.LambdaSettings.UploadURL = "https://lbda.net/";
        }

        return new Lambda(config.LambdaSettings);
    }
}

[JsonSerializable(typeof(LambdaResponse))]
internal partial class LambdaJsonContext : JsonSerializerContext;

public sealed class Lambda(LambdaSettings config) : FileUploader
{
    private const string ApiUrl = "https://lbda.net/api/upload";

    public LambdaSettings Config { get; } = config ?? throw new ArgumentNullException(nameof(config));

    public override UploadResult Upload(Stream stream, string? fileName)
    {
        var result = SendRequestFile(
            ApiUrl,
            stream,
            fileName,
            "file",
            new Dictionary<string, string?> { ["api_key"] = Config.UserAPIKey },
            method: HttpMethod.Put);

        if (string.IsNullOrWhiteSpace(result.Response))
        {
            result.IsSuccess = false;
            Errors.Add("Lambda returned an empty response. Verify the API key and service availability.");
            return result;
        }

        LambdaResponse? response;
        try
        {
            response = JsonSerializer.Deserialize(result.Response, LambdaJsonContext.Default.LambdaResponse);
        }
        catch (JsonException ex)
        {
            result.IsSuccess = false;
            Errors.Add("Lambda returned invalid JSON: " + ex.Message);
            return result;
        }

        if (!result.IsSuccess || response is null || string.IsNullOrWhiteSpace(response.Url))
        {
            result.IsSuccess = false;
            foreach (var error in response?.Errors ?? [])
            {
                if (!string.IsNullOrWhiteSpace(error)) Errors.Add(error);
            }

            if (Errors.Count == 0) Errors.Add("Lambda did not return an upload URL.");
            return result;
        }

        if (!UploaderResponseValidator.TryResolveHttpUri(Config.UploadURL, response.Url, out var uri))
        {
            result.IsSuccess = false;
            Errors.Add("Lambda returned an invalid upload URL.");
            return result;
        }

        result.URL = uri!.AbsoluteUri;
        return result;
    }
}

public sealed class LambdaSettings
{
    [JsonEncrypt]
    [YamlEncrypt]
    public string UserAPIKey { get; set; } = "";

    public string UploadURL { get; set; } = "https://lbda.net/";
}

public sealed class LambdaResponse
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("errors")]
    public List<string>? Errors { get; set; }
}
