namespace Iverson.Client.Attributes;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonEmbeddingAttribute : Attribute
{
    public EmbeddingModel Model     { get; }
    public int            Dimension { get; }

    public IversonEmbeddingAttribute(EmbeddingModel model, int dimension = 0)
    {
        Model     = model;
        Dimension = model == EmbeddingModel.Custom
            ? (dimension > 0
                ? dimension
                : throw new ArgumentException(
                    "A positive dimension is required when using EmbeddingModel.Custom.", nameof(dimension)))
            : model.GetDimension();
    }
}
