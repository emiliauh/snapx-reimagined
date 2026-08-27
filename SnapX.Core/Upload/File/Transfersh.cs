// SPDX-License-Identifier: GPL-3.0-or-later

using SnapX.Core.Upload.BaseServices;
using SnapX.Core.Upload.BaseUploaders;
using SnapX.Core.Upload.Utils;

namespace SnapX.Core.Upload.File;

public sealed class TransfershFileUploaderService : FileUploaderService
{
    public override FileDestination EnumValue => FileDestination.Transfersh;

    public override bool CheckConfig(UploadersConfig config) => true;

    public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo) => new Transfersh();
}

public sealed class Transfersh : FileUploader
{
    private const string UploadUrl = "https://transfer.sh";

    public override UploadResult Upload(Stream stream, string? fileName)
    {
        var result = SendRequestFile(UploadUrl, stream, fileName, "file");

        if (!result.IsSuccess)
        {
            return result;
        }

        if (UploaderResponseValidator.TryGetHttpUri(result.Response, out var uri, "transfer.sh"))
        {
            result.URL = uri!.AbsoluteUri;
        }
        else
        {
            result.IsSuccess = false;
            Errors.Add("transfer.sh returned a successful status without a valid transfer.sh URL.");
        }

        return result;
    }
}
