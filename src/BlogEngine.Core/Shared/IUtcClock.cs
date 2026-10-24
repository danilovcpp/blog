namespace BlogEngine.Core.Shared;

public interface IUtcClock
{
    DateTimeOffset UtcNow { get; }
}
