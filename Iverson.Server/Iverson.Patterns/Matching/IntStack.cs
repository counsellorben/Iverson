// Port of Trino 483 io.trino.operator.window.matcher.IntStack (Apache-2.0); see THIRD-PARTY-NOTICES.md.
namespace Iverson.Patterns.Matching;

internal sealed class IntStack
{
    private int[] _values;
    private int _next;

    public IntStack(int capacity)
    {
        _values = new int[capacity];
    }

    public void Push(int value)
    {
        EnsureCapacity();
        _values[_next] = value;
        _next++;
    }

    public int Pop()
    {
        _next--;
        return _values[_next];
    }

    public int Size => _next;

    private void EnsureCapacity()
    {
        if (_next == _values.Length)
        {
            Array.Resize(ref _values, _next * 2 + 1);
        }
    }
}
