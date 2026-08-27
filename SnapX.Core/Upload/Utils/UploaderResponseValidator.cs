// SPDX-License-Identifier: GPL-3.0-or-later

namespace SnapX.Core.Upload.Utils;

public static class UploaderResponseValidator
{
    public static bool TryGetHttpUri(string? value, out Uri? uri, params string[] allowedHosts)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(parsed.Host))
        {
            return false;
        }

        if (allowedHosts.Length > 0 && !allowedHosts.Any(host => HostMatches(parsed.Host, host)))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool TryResolveHttpUri(string? baseUrl, string? value, out Uri? uri)
    {
        uri = null;

        if (!TryGetHttpUri(baseUrl, out var baseUri) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUri, value.Trim(), out var resolved) ||
            (resolved.Scheme != Uri.UriSchemeHttps && resolved.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        uri = resolved;
        return true;
    }

    private static bool HostMatches(string actualHost, string expectedHost)
    {
        var normalized = expectedHost.Trim().TrimEnd('.');
        return actualHost.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
               actualHost.EndsWith('.' + normalized, StringComparison.OrdinalIgnoreCase);
    }
}
