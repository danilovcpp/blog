namespace BlogEngine.Core.Engagement.Contracts;

public enum VisitorKind : short { Authenticated = 1, Anonymous = 2 }

public sealed class VisitorIdentity
{
    private readonly byte[] key;
    public VisitorIdentity(VisitorKind kind, ReadOnlySpan<byte> key)
    {
        if (kind is not (VisitorKind.Authenticated or VisitorKind.Anonymous) || key.Length != 32)
        { throw new ArgumentException("A visitor requires a known kind and a 32-byte key."); }
        Kind = kind; this.key = key.ToArray();
    }
    public VisitorKind Kind { get; }
    public ReadOnlyMemory<byte> Key => key;
}
