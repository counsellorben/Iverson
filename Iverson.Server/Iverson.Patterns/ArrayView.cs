namespace Iverson.Patterns;

/// <summary>A read-only view over the first <see cref="Length"/> items of an int array (port of Trino 483
/// <c>io.trino.operator.window.matcher.ArrayView</c>, Apache-2.0; see THIRD-PARTY-NOTICES.md).</summary>
internal readonly struct ArrayView(int[] array, int length)
{
    public static readonly ArrayView Empty = new([], 0);

    public int Length { get; } = length;

    public int this[int index] =>
        (uint)index < (uint)Length ? array[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public int[] ToArray() => array.AsSpan(0, Length).ToArray();
}
