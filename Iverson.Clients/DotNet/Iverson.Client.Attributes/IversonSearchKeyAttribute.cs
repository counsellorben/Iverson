namespace Iverson.Client.Attributes;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonSearchKeyAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}
