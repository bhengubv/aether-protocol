// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public record AttachmentRef(string Hash, string ContentType, long Bytes)
{
    public const char Start = '';
    public const char Field = '';
}
