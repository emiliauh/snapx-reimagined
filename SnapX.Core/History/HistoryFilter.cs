
// SPDX-License-Identifier: GPL-3.0-or-later


using System.Text.RegularExpressions;

namespace SnapX.Core.History;

public record HistoryFilter
{
    private const int MaximumSearchPatternLength = 512;
    public string Filename { get; set; } = string.Empty;
    public string URL { get; set; } = string.Empty;
    public bool FilterDate { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public bool FilterType { get; set; }
    public string Type { get; set; } = string.Empty;
    public bool FilterHost { get; set; }
    public string Host { get; set; } = string.Empty;

    public int MaxItemCount { get; set; }
    public bool SearchInTags { get; set; } = true;

    public HistoryFilter()
    {
    }

    public IEnumerable<HistoryItem> ApplyFilter(IEnumerable<HistoryItem> historyItems)
    {
        ArgumentNullException.ThrowIfNull(historyItems);
        historyItems = historyItems.Where(x => x is not null);

        if (FilterType && !string.IsNullOrEmpty(Type))
        {
            historyItems = historyItems.Where(x => !string.IsNullOrEmpty(x.Type) && x.Type.Equals(Type, StringComparison.InvariantCultureIgnoreCase));
        }

        if (FilterHost && !string.IsNullOrEmpty(Host))
        {
            historyItems = historyItems.Where(x => !string.IsNullOrEmpty(x.Host) && x.Host.Contains(Host, StringComparison.InvariantCultureIgnoreCase));
        }

        if (!string.IsNullOrEmpty(Filename))
        {
            if (Filename.Length > MaximumSearchPatternLength)
                return Enumerable.Empty<HistoryItem>();

            string pattern = Regex.Escape(Filename).Replace("\\?", ".").Replace("\\*", ".*");
            Regex regex = new(
                pattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(250));
            historyItems = historyItems.Where(x => IsMatch(regex, x.FileName) ||
                (SearchInTags && x.Tags != null && x.Tags.Any(tag =>
                    tag is not null && IsMatch(regex, tag.Text))));
        }

        if (!string.IsNullOrEmpty(URL))
        {
            historyItems = historyItems.Where(x => x.URL != null && x.URL.Contains(URL, StringComparison.InvariantCultureIgnoreCase));
        }

        if (FilterDate)
        {
            historyItems = historyItems.Where(x => x.DateTime.Date >= FromDate && x.DateTime.Date <= ToDate);
        }

        if (MaxItemCount > 0)
        {
            historyItems = historyItems.Take(MaxItemCount);
        }

        return historyItems;
    }

    private static bool IsMatch(Regex regex, string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
