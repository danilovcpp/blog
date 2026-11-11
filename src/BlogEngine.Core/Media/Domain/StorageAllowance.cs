using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Domain;

public sealed class StorageAllowance
{
    private StorageAllowance() { }
    public Guid OwnerId { get; private set; }
    public long LimitBytes { get; private set; } = MediaRules.DefaultAllowance;
    public long UsedBytes { get; private set; }
    public long Available(long reservedBytes) => checked(LimitBytes - UsedBytes - reservedBytes);
    public void ApplyUsage(long delta)
    {
        var next = checked(UsedBytes + delta);
        if (next < 0 || next > LimitBytes) { throw new DomainError("quota_exceeded"); }
        UsedBytes = next;
    }
}
