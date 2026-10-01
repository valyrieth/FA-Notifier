namespace FaNotify.State;

internal sealed class NotificationState
{
    private const int MaxSeenItems = 5000;

    // Trimming only after a margin of growth keeps the sort off most saves.
    private const int TrimThreshold = 6000;

    public Dictionary<string, DateTimeOffset> SeenItems { get; set; } = new(StringComparer.Ordinal);

    public void TrimSeenItems()
    {
        if (SeenItems.Count > TrimThreshold)
        {
            SeenItems = SeenItems
                .OrderByDescending(item => item.Value)
                .Take(MaxSeenItems)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        }
    }
}
