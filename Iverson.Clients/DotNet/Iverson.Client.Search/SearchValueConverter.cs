using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>
/// Converts CLR values into <see cref="SearchValue"/> wire values.
/// Shared by <see cref="QueryBuilder{T}"/> and <see cref="GroupByBuilder"/> so the
/// value-mapping logic has a single implementation instead of two that can silently diverge.
/// </summary>
internal static class SearchValueConverter
{
    public static SearchValue ToSearchValue(object? value) => value switch
    {
        null          => new SearchValue(),
        string s      => new SearchValue { StringVal  = s },
        bool b        => new SearchValue { BoolVal    = b },
        float f       => new SearchValue { NumberVal  = f },
        double d      => new SearchValue { NumberVal  = d },
        int i         => new SearchValue { NumberVal  = i },
        long l        => new SearchValue { NumberVal  = l },
        DateTime dt   => new SearchValue { StringVal  = dt.ToString("o") },
        DateTimeOffset dto => new SearchValue { StringVal = dto.ToString("o") },

        // IN operator: IEnumerable<string>
        IEnumerable<string> strings => new SearchValue
        {
            StringList = new RepeatedString { Values = { strings } }
        },

        // VECTOR_SIMILAR operator: float[]
        float[] floats => new SearchValue
        {
            FloatList = new RepeatedFloat { Values = { floats } }
        },

        // Fallback: toString
        _ => new SearchValue { StringVal = value.ToString() ?? string.Empty }
    };
}
