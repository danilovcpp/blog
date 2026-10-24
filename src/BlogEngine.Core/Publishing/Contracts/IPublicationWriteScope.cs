namespace BlogEngine.Core.Publishing.Contracts;

// The implementation owns the single publication lock and transaction. Related modules do not commit independently.
public interface IPublicationWriteScope
{
    Task<T> ExecuteAsync<T>(Guid publicationId,
        Func<IPublicationWriteSession, CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

public interface IPublicationWriteSession
{
    PublicationAccess Publication { get; }
    DateTimeOffset UtcNow { get; }
    Task AdjustCountersAsync(CounterDelta delta, CancellationToken cancellationToken);
}

public readonly record struct CounterDelta
{
    public CounterDelta(int likes, int views)
    {
        if (likes is < -1 or > 1) { throw new ArgumentOutOfRangeException(nameof(likes)); }
        if (views is < 0 or > 1) { throw new ArgumentOutOfRangeException(nameof(views)); }
        Likes = likes;
        Views = views;
    }
    public int Likes { get; }
    public int Views { get; }
}
