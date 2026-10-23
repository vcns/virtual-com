using NovaStarProxy.Transport;

namespace NovaStarProxy.Tests;

/// <summary>A stand-in for the proxy's side of a virtual port that records modem-line changes.</summary>
internal sealed class TestEndpoint(Stream stream) : IPortEndpoint
{
    private readonly List<bool> _linkChanges = [];

    public Stream Stream { get; } = stream;

    public IReadOnlyList<bool> LinkChanges
    {
        get
        {
            lock (_linkChanges)
                return _linkChanges.ToArray();
        }
    }

    public void SetLinkUp(bool up)
    {
        lock (_linkChanges)
            _linkChanges.Add(up);
    }

    public string? DescribePeer() => "test peer";

    public void Dispose() => Stream.Dispose();
}
