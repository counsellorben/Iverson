namespace Iverson.Client.Attributes;

/// <summary>
/// Marks the UTC <see cref="DateTime"/> property recording when an interaction happened.
/// Exactly one property per entity may carry this; two fail schema registration.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonPopularitySignalAttribute : Attribute;
