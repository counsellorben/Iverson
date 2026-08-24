using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.StarRocks;
using System.Text;

namespace Iverson.Api.Grpc;

public interface IPayloadSizeValidator
{
    void ValidateTextColumnSizes(Struct payload, SchemaDescriptor schema);
}

/// <summary>
/// Rejects a write whose text values will not fit the StarRocks columns they are bound for.
///
/// <para><b>Why this runs at the write call rather than being handled downstream.</b> An oversized
/// value is FILTERED OUT by StarRocks' insert, which raises an ordinary exception on the Kafka
/// projection — so <c>MessageDispatcher</c> retried it and then dead-lettered it, long after
/// <c>Post</c> had returned success to the caller. The document remained in Postgres and remained
/// findable through <c>SearchSimilar</c>/<c>SearchChunks</c> (which read Qdrant, not StarRocks),
/// while <c>Search</c>, <c>Aggregate</c> and <c>GroupBy</c> could not see it at all. Nothing told
/// the caller. Failing here converts a silent divergence between the two stores into an error the
/// writer receives, at the only point where the writer can still do something about it.</para>
///
/// <para><b>This is a deliberate loss of behaviour, chosen knowingly.</b> A document over the limit
/// used to persist to Postgres and stay vector-searchable. It is now refused outright. The
/// alternative — truncating the analytics copy — keeps that document working at the cost of
/// analytics quietly holding a partial value, which is the same class of silent divergence this
/// exists to remove.</para>
///
/// <para>Sizes are measured in UTF-8 BYTES. StarRocks' <c>VARCHAR(N)</c> counts bytes, not
/// characters — four multi-byte characters do not fit a <c>VARCHAR(4)</c> — so measuring
/// <c>string.Length</c> would accept values StarRocks then drops, reintroducing the defect for
/// exactly the non-ASCII content most likely to be near the limit.</para>
/// </summary>
public sealed class PayloadSizeValidator : IPayloadSizeValidator
{
    public void ValidateTextColumnSizes(Struct payload, SchemaDescriptor schema)
    {
        // Only types actually projected to StarRocks can hit this. A type that is not projected has
        // no column to overflow, and failing its writes would be a rule invented here rather than
        // one the store imposes.
        if (!StoreTargeting.DetermineTargetStores(schema).HasFlag(StoreTarget.Engagement))
            return;

        foreach (var column in schema.ScalarColumns)
        {
            if (!payload.Fields.TryGetValue(column.Name, out var value) ||
                value.KindCase != Value.KindOneofCase.StringValue)
            {
                continue;
            }

            // The cap is the one the PROJECTION will actually use for this column, read from the
            // same place SchemaBuilder reads it. Hardcoding either limit here would let the two
            // drift, and a drift in the permissive direction restores the original defect.
            var maxBytes = StarRocksLimits.MaxBytesForTextColumn(
                schema.LargeFieldColumns.Contains(column.Name));

            var actualBytes = Encoding.UTF8.GetByteCount(value.StringValue);
            if (actualBytes <= maxBytes)
                continue;

            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"Field '{column.Name}' is {actualBytes:N0} bytes, which exceeds the {maxBytes:N0}-byte " +
                $"limit of the StarRocks column it is stored in. The value is measured in UTF-8 bytes, " +
                $"not characters. " +
                (schema.LargeFieldColumns.Contains(column.Name)
                    ? "This field is already stored in the widest column StarRocks supports; split the " +
                      "value across records, or store it outside the mapped payload."
                    : "Mark the property as a large field ([IversonLargeField], [IversonChunk] or " +
                      $"[IversonEmbedding]) to raise its limit to {StarRocksLimits.MaxVarcharBytes:N0} bytes.")));
        }
    }
}
