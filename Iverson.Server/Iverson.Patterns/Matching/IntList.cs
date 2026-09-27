// Port of Trino 483 io.trino.operator.window.matcher.IntList (Apache-2.0); see THIRD-PARTY-NOTICES.md.
namespace Iverson.Patterns.Matching;

internal sealed class IntList
{
    private int[] _values;
    private int _next;

    public IntList(int capacity)
    {
        _values = new int[capacity];
    }

    private IntList(int[] values, int next)
    {
        _values = values;
        _next = next;
    }

    public void Add(int value)
    {
        EnsureCapacity(_next);
        _values[_next] = value;
        _next++;
    }

    public int Get(int index) => _values[index];

    public void Set(int index, int value)
    {
        EnsureCapacity(index);
        _values[index] = value;
        _next = Math.Max(_next, index + 1);
    }

    public int Size => _next;

    public void Clear() => _next = 0;

    public IntList Copy() => new((int[])_values.Clone(), _next);

    public ArrayView ToArrayView() => new(_values, _next);

    private void EnsureCapacity(int index)
    {
        if (index >= _values.Length)
        {
            Array.Resize(ref _values, Math.Max(_values.Length * 2, index + 1));
        }
    }
}
