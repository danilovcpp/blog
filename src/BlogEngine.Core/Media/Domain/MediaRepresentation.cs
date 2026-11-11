using BlogEngine.Core.Shared;

namespace BlogEngine.Core.Media.Domain;

public sealed class MediaRepresentation
{
    private MediaRepresentation() { }
    public Guid FileId { get; private set; }
    public MediaObjectKind Kind { get; private set; }
    public Guid ObjectId { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Mime { get; private set; } = "image/png";
    public long VerifiedLength { get; private set; }
    public byte[] Sha256 { get; private set; } = [];

    public static MediaRepresentation Create(MediaObject obj, int width, int height)
    {
        if (obj.State != MediaObjectState.Present || width <= 0 || height <= 0 || obj.Kind switch
        {
            MediaObjectKind.Display => width > 2560 || height > 2560,
            MediaObjectKind.Avatar256 => width != 256 || height != 256,
            MediaObjectKind.Avatar512 => width != 512 || height != 512,
            _ => true,
        }) { throw new DomainError("invalid_state"); }
        return new()
        {
            FileId = obj.FileId,
            Kind = obj.Kind,
            ObjectId = obj.Id,
            Width = width,
            Height = height,
            VerifiedLength = obj.VerifiedLength!.Value,
            Sha256 = obj.Sha256!.ToArray()
        };
    }
}
