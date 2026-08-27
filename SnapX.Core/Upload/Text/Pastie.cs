// SPDX-License-Identifier: GPL-3.0-or-later

using SnapX.Core.Upload.BaseServices;
using SnapX.Core.Upload.BaseUploaders;
using SnapX.Core.Upload.Utils;

namespace SnapX.Core.Upload.Text;

public sealed class PastieTextUploaderService : TextUploaderService
{
    public override TextDestination EnumValue => TextDestination.Pastie;

    public override bool CheckConfig(UploadersConfig config) => true;

    public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo) =>
        new Pastie { IsPublic = config.PastieIsPublic };
}

public sealed class Pastie : TextUploader
{
    private const string ApiUrl = "http://pastie.org/pastes";

    public bool IsPublic { get; set; }

    public override UploadResult UploadText(string? text, string? fileName)
    {
        var result = new UploadResult();
        if (string.IsNullOrEmpty(text)) return result;

        var arguments = new Dictionary<string, string?>
        {
            ["paste[body]"] = text,
            ["paste[restricted]"] = IsPublic ? "0" : "1",
            ["paste[authorization]"] = "burger"
        };

        result.Response = SendRequestURLEncoded(HttpMethod.Post, ApiUrl, arguments);
        result.ResponseInfo = LastResponseInfo;
        result.IsSuccess = LastResponseInfo?.IsSuccess == true;

        if (result.IsSuccess &&
            UploaderResponseValidator.TryGetHttpUri(LastResponseInfo?.ResponseURL, out var uri, "pastie.org"))
        {
            result.URL = uri!.AbsoluteUri;
        }
        else
        {
            result.IsSuccess = false;
            Errors.Add("Pastie did not return a valid paste URL. The legacy Pastie API may be unavailable.");
        }

        return result;
    }
}
