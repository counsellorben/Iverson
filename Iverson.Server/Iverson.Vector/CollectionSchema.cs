namespace Iverson.Vector;

public sealed record CollectionSchema(
    string CollectionName,
    IReadOnlyList<NamedVector> Vectors,
    IReadOnlyList<PayloadIndex> PayloadIndexes);

public sealed record NamedVector(string Name, int Dimension);

public sealed record PayloadIndex(string FieldName, PayloadIndexKind Kind);

public enum PayloadIndexKind { Keyword, Integer, Float, Boolean, Datetime }
