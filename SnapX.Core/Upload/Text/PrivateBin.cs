// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnapX.Core.Upload.BaseServices;
using SnapX.Core.Upload.BaseUploaders;
using SnapX.Core.Upload.Utils;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Extensions;

namespace SnapX.Core.Upload.Text;

public sealed class PrivateBinUploaderService : TextUploaderService
{
    public override TextDestination EnumValue => TextDestination.PrivateBin;

    public override bool CheckConfig(UploadersConfig config) =>
        config.PrivateBinSettings is not null &&
        UploaderResponseValidator.TryGetHttpUri(config.PrivateBinSettings.CustomUrl, out _);

    public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo) =>
        new PrivateBin(config.PrivateBinSettings ?? new PrivateBinSettings());
}

public sealed class PrivateBin(PrivateBinSettings settings) : TextUploader
{
    private const int IterationCount = 100_000;
    private const int KeyBits = 256;
    private const int IvBits = 128;

    public PrivateBinSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    public override UploadResult UploadText(string? text, string? fileName)
    {
        var result = new UploadResult();
        if (string.IsNullOrEmpty(text)) return result;

        if (!UploaderResponseValidator.TryGetHttpUri(Settings.CustomUrl, out var serviceUri))
        {
            Errors.Add("PrivateBin requires an absolute HTTP or HTTPS service URL.");
            return result;
        }

        var crypto = new PrivateBinCrypto(Settings);
        var payload = BuildPayload(crypto, text);
        var headers = new NameValueCollection { ["X-Requested-With"] = "JSONHttpRequest" };

        if (!string.IsNullOrEmpty(Settings.Username) || !string.IsNullOrEmpty(Settings.Password))
        {
            if (string.IsNullOrEmpty(Settings.Username) || string.IsNullOrEmpty(Settings.Password))
            {
                Errors.Add("PrivateBin basic authentication requires both a username and password.");
                return result;
            }

            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(Settings.Username + ':' + Settings.Password));
            headers["Authorization"] = "Basic " + token;
        }

        result.Response = SendRequest(HttpMethod.Post, serviceUri!.AbsoluteUri, payload, RequestHelpers.ContentTypeJSON, headers: headers);
        result.ResponseInfo = LastResponseInfo;
        result.IsSuccess = LastResponseInfo?.IsSuccess == true;

        if (string.IsNullOrWhiteSpace(result.Response))
        {
            result.IsSuccess = false;
            Errors.Add("PrivateBin returned an empty response.");
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Response);
            var root = document.RootElement;
            var status = GetInt32(root, "status");

            if (!result.IsSuccess || status != 0)
            {
                result.IsSuccess = false;
                var message = GetString(root, "message");
                Errors.Add(!string.IsNullOrWhiteSpace(message) ? message : "PrivateBin rejected the paste.");
                return result;
            }

            var responseUrl = GetString(root, "url");
            var pasteId = GetString(root, "id");
            var deleteToken = GetString(root, "deletetoken");

            if (!UploaderResponseValidator.TryResolveHttpUri(serviceUri.AbsoluteUri, responseUrl, out var pasteUri))
            {
                result.IsSuccess = false;
                Errors.Add("PrivateBin returned an invalid paste URL.");
                return result;
            }

            result.URL = new UriBuilder(pasteUri!) { Fragment = crypto.PublicDecryptionKey }.Uri.AbsoluteUri;

            if (!string.IsNullOrWhiteSpace(pasteId) && !string.IsNullOrWhiteSpace(deleteToken))
            {
                var deletion = new UriBuilder(serviceUri)
                {
                    Query = "pasteid=" + Uri.EscapeDataString(pasteId) +
                            "&deletetoken=" + Uri.EscapeDataString(deleteToken)
                };
                result.DeletionURL = deletion.Uri.AbsoluteUri;
            }
        }
        catch (JsonException ex)
        {
            result.IsSuccess = false;
            Errors.Add("PrivateBin returned invalid JSON: " + ex.Message);
        }

        return result;
    }

    private string BuildPayload(PrivateBinCrypto crypto, string text)
    {
        var metadata = new JsonArray
        {
            Convert.ToBase64String(crypto.Iv),
            Convert.ToBase64String(crypto.Salt),
            IterationCount,
            KeyBits,
            IvBits,
            "aes",
            "cbc",
            "none"
        };

        var associatedData = new JsonArray
        {
            metadata,
            Settings.Format.GetDescription(),
            0,
            Settings.BurnAfterReading ? 1 : 0
        };

        return new JsonObject
        {
            ["adata"] = associatedData,
            ["meta"] = new JsonObject { ["expire"] = Settings.Expiration.GetDescription() },
            ["v"] = 2,
            ["ct"] = crypto.EncryptMessage(text, Settings.PastePassword)
        }.ToJsonString();
    }

    private static int? GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class PrivateBinCrypto
    {
        private byte[] passwordBytes;

        public byte[] Iv { get; } = RandomNumberGenerator.GetBytes(IvBits / 8);
        public byte[] Salt { get; } = RandomNumberGenerator.GetBytes(8);
        public string PublicDecryptionKey { get; }

        public PrivateBinCrypto(PrivateBinSettings settings)
        {
            passwordBytes = RandomNumberGenerator.GetBytes(KeyBits / 8);
            PublicDecryptionKey = Base58Encode(passwordBytes);
        }

        public string EncryptMessage(string message, string? pastePassword)
        {
            if (!string.IsNullOrEmpty(pastePassword))
            {
                passwordBytes = [.. passwordBytes, .. Encoding.UTF8.GetBytes(pastePassword)];
            }

            var plaintext = JsonSerializer.Serialize(new Dictionary<string, string> { ["paste"] = message });
            using var deriveBytes = new Rfc2898DeriveBytes(passwordBytes, Salt, IterationCount, HashAlgorithmName.SHA256);
            using var aes = Aes.Create();
            aes.Key = deriveBytes.GetBytes(KeyBits / 8);
            aes.IV = Iv;
            aes.Mode = CipherMode.CBC;

            using var output = new MemoryStream();
            using (var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write))
            using (var writer = new StreamWriter(crypto, new UTF8Encoding(false)))
            {
                writer.Write(plaintext);
            }

            return Convert.ToBase64String(output.ToArray());
        }

        private static string Base58Encode(ReadOnlySpan<byte> data)
        {
            const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
            if (data.IsEmpty) return "";

            var digits = new byte[data.Length * 138 / 100 + 1];
            var digitLength = 1;

            foreach (var value in data)
            {
                var carry = (int)value;
                for (var i = 0; i < digitLength; i++)
                {
                    carry += digits[i] << 8;
                    digits[i] = (byte)(carry % 58);
                    carry /= 58;
                }

                while (carry > 0)
                {
                    digits[digitLength++] = (byte)(carry % 58);
                    carry /= 58;
                }
            }

            var builder = new StringBuilder(data.Length * 2);
            foreach (var value in data)
            {
                if (value != 0) break;
                builder.Append('1');
            }

            for (var i = digitLength - 1; i >= 0; i--)
            {
                builder.Append(alphabet[digits[i]]);
            }

            return builder.ToString();
        }
    }
}

[DefaultValue(PrivateBinExpiration.W1)]
public enum PrivateBinExpiration
{
    [Description("5min")] M5,
    [Description("10min")] M10,
    [Description("1hour")] H1,
    [Description("1day")] D1,
    [Description("1week")] W1,
    [Description("1month")] M1,
    [Description("1year")] Y1,
    [Description("never")] N
}

[DefaultValue(PrivateBinFormat.PlainText)]
public enum PrivateBinFormat
{
    [Description("plaintext")] PlainText,
    [Description("syntaxhighlighting")] SyntaxHighlighting,
    [Description("markdown")] Markdown
}

public sealed class PrivateBinSettings
{
    public string Username { get; set; } = "";

    [JsonEncrypt]
    [YamlEncrypt]
    public string Password { get; set; } = "";

    public bool BurnAfterReading { get; set; }
    public PrivateBinExpiration Expiration { get; set; } = PrivateBinExpiration.W1;
    public PrivateBinFormat Format { get; set; } = PrivateBinFormat.PlainText;
    public string CustomUrl { get; set; } = "https://privatebin.net/";

    [JsonEncrypt]
    [YamlEncrypt]
    public string PastePassword { get; set; } = "";
}
