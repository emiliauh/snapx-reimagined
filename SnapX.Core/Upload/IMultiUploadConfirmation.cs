// SPDX-License-Identifier: GPL-3.0-or-later

namespace SnapX.Core.Upload;

public readonly record struct MultiUploadConfirmationResult(bool Confirmed, bool SuppressFutureWarning = false);

public interface IMultiUploadConfirmation
{
    MultiUploadConfirmationResult Confirm(int fileCount);
}

public sealed class HeadlessMultiUploadConfirmation : IMultiUploadConfirmation
{
    public static HeadlessMultiUploadConfirmation Instance { get; } = new();

    private HeadlessMultiUploadConfirmation()
    {
    }

    public MultiUploadConfirmationResult Confirm(int fileCount) => new(false);
}
