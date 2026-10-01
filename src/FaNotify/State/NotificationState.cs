namespace FaNotify.State;

internal sealed class NotificationState
{
    public Dictionary<string, DateTimeOffset> SeenItems { get; set; } = new(StringComparer.Ordinal);

    public void TrimSeenItems()
    {
        if (SeenItems.Count > 5000)
        {
            SeenItems = SeenItems
                .OrderByDescending(item => item.Value)
                .Take(5000)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        }
    }
}
