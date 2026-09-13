
// SPDX-License-Identifier: GPL-3.0-or-later


using System.Xml.Linq;
using SnapX.Core.Upload.BaseUploaders;
using SnapX.Core.Utils.Extensions;

namespace SnapX.Core.Upload.Img;

public class UploadScreenshot : ImageUploader
{
    private string APIKey { get; set; }

    public UploadScreenshot(string key)
    {
        APIKey = key;
    }

    public override UploadResult Upload(Stream stream, string? fileName)
    {
        var arguments = new Dictionary<string, string?>
        {
            { "apiKey", APIKey },
            { "xmlOutput", "1" }
        };
        //arguments.Add("testMode", "1");

        var result = SendRequestFile("https://img1.uploadscreenshot.com/api-upload.php", stream, fileName, "userfile", arguments);

        return ParseResult(result);
    }

    private UploadResult ParseResult(UploadResult result)
    {
        if (result.IsSuccess)
        {
            XDocument xdoc = XDocument.Parse(result.Response);
            XElement xele = xdoc.Root.Element("upload");

            string? error = xele.GetElementValue("errorCode");
            if (!string.IsNullOrEmpty(error))
            {
                string? errorMessage;

                switch (error)
                {
                    case "1":
                        errorMessage = "The MD5 value does not match the MD5 value of the uploaded image file." +
                                       " A network interruption can cause this error. Try the upload again.";
                        break;
                    case "2":
                        errorMessage = "The API key does not exist or the service blocked it. Contact the service administrator.";
                        break;
                    case "3":
                        errorMessage = "The file is not a PNG or JPEG file.";
                        break;
                    case "4":
                        errorMessage = "The file is larger than the 50 MB limit.";
                        break;
                    case "99":
                    default:
                        errorMessage = "An unknown error occurred. Contact the service administrator and include a copy of the file.";
                        break;
                }

                Errors.Add(errorMessage);
            }
            else
            {
                result.URL = xele.GetElementValue("original");
                result.ThumbnailURL = xele.GetElementValue("small");
                result.DeletionURL = xele.GetElementValue("deleteurl");
            }
        }

        return result;
    }
}
