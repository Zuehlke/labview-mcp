using Grpc.Core;

namespace LabVIEWMcp.Tests.Fakes;

/// <summary>
/// Minimal <see cref="IAsyncStreamReader{T}"/> for testing Rpc.CollectAsync in isolation:
/// yields the given items, then either ends the stream or hangs so the caller's local
/// budget has to fire.
/// </summary>
internal sealed class FakeStreamReader<T>(
    IEnumerable<T> items, TimeSpan itemDelay = default, bool hangAtEnd = false)
    : IAsyncStreamReader<T>
{
    private readonly IEnumerator<T> _items = items.GetEnumerator();

    public T Current { get; private set; } = default!;

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (itemDelay > TimeSpan.Zero)
            await Task.Delay(itemDelay, cancellationToken);

        if (_items.MoveNext())
        {
            Current = _items.Current;
            return true;
        }

        if (hangAtEnd)
            await Task.Delay(Timeout.Infinite, cancellationToken);

        return false;
    }
}
