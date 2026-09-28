using FluentAssertions;
using Google.Protobuf;
using Xunit;
using Proto = Iverson.Client.Contracts;
using Engine = Iverson.Patterns;

namespace Iverson.Api.Tests.Grpc;

public sealed class MatchPatternProtoTests
{
    [Fact]
    public void Proto_enums_align_with_the_engine_enums_by_value_and_name()
    {
        AssertAligned<Proto.PatternRowSource, Engine.PatternSource>();
        AssertAligned<Proto.RowsPerMatch, Engine.RowsPerMatch>();
        AssertAligned<Proto.AfterMatchSkipKind, Engine.AfterMatchSkipKind>();
    }

    private static void AssertAligned<TProto, TEngine>() where TProto : struct, Enum where TEngine : struct, Enum
    {
        var proto = Enum.GetValues<TProto>().Select(v => (Convert.ToInt32(v), v.ToString())).ToList();
        var engine = Enum.GetValues<TEngine>().Select(v => (Convert.ToInt32(v), v.ToString())).ToList();
        proto.Should().Equal(engine);
    }

    [Fact]
    public void MatchPattern_request_and_response_carry_the_spec_fields()
    {
        var request = new Proto.MatchPatternRequest
        {
            TypeName = "Article", Source = Proto.PatternRowSource.Chunks, ChunkProperty = "Body",
            WhereLogic = Proto.SearchLogic.And, Pattern = "A B+", Limit = 5, TraceId = "t",
            RowsPerMatch = Proto.RowsPerMatch.AllRowsWithUnmatched,
            AfterMatch = new Proto.AfterMatchSkip { Kind = Proto.AfterMatchSkipKind.ToFirst, Variable = "B" },
        };
        request.PartitionBy.Add("AuthorId");
        request.OrderBy.Add(new Proto.SearchSort { Property = "Id" });
        request.Subsets.Add(new Proto.PatternSubset { Name = "U", Variables = { "A", "B" } });
        request.Define.Add(new Proto.NamedExpr { Name = "B", Expr = "B.x > 0" });
        request.Measures.Add(new Proto.NamedExpr { Name = "m", Expr = "COUNT(*)" });

        var roundTripped = Proto.MatchPatternRequest.Parser.ParseFrom(request.ToByteArray());
        roundTripped.Should().Be(request);

        var response = new Proto.MatchPatternResponse { MatchNumber = 3, Classifier = "B", TraceId = "t" };
        response.MatchNumber.Should().Be(3L);
    }
}
