using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SnapX.Core.Upload.Custom;
using SnapX.Core.Upload.Img;

static class LiveUploaderProbe
{
    public static int Run(string path)
    {
        try
        {
            var item = SnapX.Core.Utils.JsonHelpers.DeserializeFromFile<CustomUploaderItem>(path)
                ?? throw new InvalidDataException("No uploader definition.");
            using var image = new Image<Rgba32>(16,16,Color.CornflowerBlue);
            using var stream = new MemoryStream();
            image.SaveAsPng(stream); stream.Position=0;
            var uploader = new CustomImageUploader(item);
            var result = uploader.Upload(stream, "snapx-macos-synthetic-test.png");
            if (result?.IsSuccess != true || string.IsNullOrWhiteSpace(result.URL))
            {
                Console.WriteLine($"Configured synthetic upload failed; status={(int?)result?.ResponseInfo?.StatusCode}; errors={uploader.Errors.Count}. Response omitted to protect credentials.");
                return 1;
            }
            Console.WriteLine("Configured synthetic 16x16 PNG upload succeeded.");
            Console.WriteLine($"Result URL: {result.URL}");
            return 0;
        }
        catch(Exception ex)
        {
            Console.WriteLine($"Configured upload failed: {ex.GetType().Name}. Details omitted to protect credentials.");
            return 1;
        }
    }
}
