using BlogEngine.Core.Engagement.Contracts;

namespace BlogEngine.Core.Engagement.Domain;

public sealed class ViewDeduplicationRecord
{
    private ViewDeduplicationRecord() { }
    public Guid PublicationId { get; private set; }
    public VisitorKind VisitorKind { get; private set; }
    public byte[] VisitorKey { get; private set; } = [];
    public DateTimeOffset LastCountedAt { get; private set; }
    public static TimeSpan CountingWindow => TimeSpan.FromHours(24);
}
