// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using SnapX.Core.Upload.BaseServices;
using SnapX.Core.Upload.BaseUploaders;
using SnapX.Core.Upload.Utils;

namespace SnapX.Core.Upload.Text;

public sealed class SlexyTextUploaderService : TextUploaderService
{
    public override TextDestination EnumValue => TextDestination.Slexy;

    public override bool CheckConfig(UploadersConfig config) => true;

    public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo) =>
        new Slexy(new SlexySettings { TextFormat = taskInfo.TextFormat ?? "text" });
}

public sealed class Slexy(SlexySettings settings) : TextUploader
{
    // Slexy has not published a TLS upload endpoint; retain ShareX compatibility but
    // validate the final response URL before exposing it as a successful upload.
    private const string ApiUrl = "http://slexy.org/index.php/submit";

    public Slexy() : this(new SlexySettings())
    {
    }

    public override UploadResult UploadText(string? text, string? fileName)
    {
        var result = new UploadResult();
        if (string.IsNullOrEmpty(text)) return result;

        var arguments = new Dictionary<string, string?>
        {
            ["raw_paste"] = text,
            ["author"] = settings.Author,
            ["comment"] = "",
            ["desc"] = settings.Description,
            ["expire"] = settings.Expiration,
            ["language"] = settings.TextFormat,
            ["linenumbers"] = settings.LineNumbers ? "1" : "0",
            ["permissions"] = settings.Visibility == Privacy.Private ? "1" : "0",
            ["submit"] = "Submit Paste",
            ["tabbing"] = "true",
            ["tabtype"] = "real"
        };

        result.Response = SendRequestMultiPart(ApiUrl, arguments);
        result.ResponseInfo = LastResponseInfo;
        result.IsSuccess = LastResponseInfo?.IsSuccess == true;

        if (result.IsSuccess &&
            UploaderResponseValidator.TryGetHttpUri(LastResponseInfo?.ResponseURL, out var uri, "slexy.org"))
        {
            result.URL = uri!.AbsoluteUri;
        }
        else
        {
            result.IsSuccess = false;
            Errors.Add("Slexy did not return a valid paste URL. The legacy Slexy API may be unavailable.");
        }

        return result;
    }
}

public sealed class SlexySettings
{
    public string TextFormat { get; set; } = "text";
    public string Author { get; set; } = "";
    public Privacy Visibility { get; set; } = Privacy.Private;
    public string Description { get; set; } = "";
    public bool LineNumbers { get; set; } = true;

    [Description("Expiration time in seconds; 0 means forever.")]
    public string Expiration { get; set; } = "2592000";
}
