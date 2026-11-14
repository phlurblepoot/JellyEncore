namespace Encore.Tests.Fetch;

/// <summary>A settable <see cref="Encore.Fetch.Import.IFreeSpace"/>.</summary>
internal sealed class FakeFreeSpace(long? bytes = null) : Encore.Fetch.Import.IFreeSpace
{
    public long? Bytes { get; set; } = bytes;

    public List<string> Asked { get; } = [];

    public long? FreeBytes(string path)
    {
        Asked.Add(path);
        return Bytes;
    }
}
