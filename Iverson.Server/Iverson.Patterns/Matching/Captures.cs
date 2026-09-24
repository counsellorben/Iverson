// Port of Trino 483 io.trino.operator.window.matcher.Captures (Apache-2.0); see THIRD-PARTY-NOTICES.md.
namespace Iverson.Patterns.Matching;

/// <summary>Per thread: the exclusion capture slots (relative input indexes) and the matched labels.</summary>
internal sealed class Captures(int initialCapacity, int slotCount, int labelCount)
{
    private readonly IntMultimap _captures = new(initialCapacity, slotCount);
    private readonly IntMultimap _labels = new(initialCapacity, labelCount);

    public void Save(int threadId, int value) => _captures.Add(threadId, value);

    public void SaveLabel(int threadId, int value) => _labels.Add(threadId, value);

    public void Copy(int parent, int child)
    {
        _captures.Copy(parent, child);
        _labels.Copy(parent, child);
    }

    public ArrayView GetCaptures(int threadId) => _captures.GetArrayView(threadId);

    public ArrayView GetLabels(int threadId) => _labels.GetArrayView(threadId);

    public void Release(int threadId)
    {
        _captures.Release(threadId);
        _labels.Release(threadId);
    }
}
