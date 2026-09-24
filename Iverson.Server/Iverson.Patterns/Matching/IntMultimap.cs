// Port of Trino 483 io.trino.operator.window.matcher.IntMultimap (Apache-2.0); see THIRD-PARTY-NOTICES.md.
namespace Iverson.Patterns.Matching;

/// <summary>Key → list of ints, keyed densely by small non-negative ints (thread ids or instruction pointers).
/// Memory accounting (<c>getSizeInBytes</c>) is dropped.</summary>
internal sealed class IntMultimap
{
    private IntList?[] _values;
    private readonly int _capacity;
    private readonly int _listCapacity;

    public IntMultimap(int capacity, int listCapacity)
    {
        _values = new IntList?[capacity];
        _capacity = capacity;
        _listCapacity = listCapacity;
    }

    public void Add(int key, int value)
    {
        bool expanded = EnsureCapacity(key);
        if (expanded || _values[key] is null)
        {
            _values[key] = new IntList(_listCapacity);
        }

        _values[key]!.Add(value);
    }

    public void Release(int key)
    {
        _values[key] = null;
    }

    public void Copy(int parent, int child)
    {
        bool expanded = EnsureCapacity(child);
        if (expanded || _values[child] is null)
        {
            if (_values[parent] is { } parentList)
            {
                _values[child] = parentList.Copy();
            }
        }
        else if (_values[parent] is { } parentList)
        {
            _values[child] = parentList.Copy();
        }
        else
        {
            _values[child] = null;
        }
    }

    public ArrayView GetArrayView(int key) => _values[key] is { } list ? list.ToArrayView() : ArrayView.Empty;

    public void Clear()
    {
        _values = new IntList?[_capacity];
    }

    // returns true if the array was expanded; otherwise returns false
    private bool EnsureCapacity(int key)
    {
        if (key >= _values.Length)
        {
            Array.Resize(ref _values, Math.Max(_values.Length * 2, key + 1));
            return true;
        }

        return false;
    }
}
