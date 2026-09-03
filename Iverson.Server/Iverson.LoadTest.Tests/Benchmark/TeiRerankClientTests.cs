using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Iverson.LoadTest.Benchmark;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class TeiRerankClientTests
{
    // Mirrors Iverson.Embeddings.Tests/EmbeddingServiceTests.cs:14-30. Records every request body so
    // batching can be asserted, and answers each /rerank batch from a caller-supplied function so the
    // response can deliberately come back in score-descending order, as TEI's does.
    private sealed class FakeHandler(Func<int, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Bodies.Add(body);
            return respond(Bodies.Count - 1, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TeiRerankClient Client(FakeHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://reranker.test/") });

    // TEI returns each batch sorted by score DESCENDING with `index` relative to the batch.
    private static string RerankResponseFor(string body)
    {
        var texts = JsonDocument.Parse(body).RootElement.GetProperty("texts").EnumerateArray()
            .Select(t => t.GetString()!).ToList();
        // Score = the number embedded in the text ("t7" -> 7), so expected scores are known.
        var scored = texts.Select((t, i) => (Index: i, Score: double.Parse(t[1..]))).OrderByDescending(s => s.Score);
        return "[" + string.Join(",", scored.Select(s => $"{{\"index\":{s.Index},\"score\":{s.Score}}}")) + "]";
    }

    [Fact]
    public async Task ScoreAsync_BatchesAtEight_AndMapsScoresBackByIndex()
    {
        var handler = new FakeHandler((_, body) => Json(RerankResponseFor(body)));
        var texts   = Enumerable.Range(0, 50).Select(i => $"t{(i * 7) % 50}").ToList();

        var scores = await Client(handler).ScoreAsync("q", texts, CancellationToken.None);

        handler.Bodies.Should().HaveCount(7);                                   // 8,8,8,8,8,8,2
        handler.Bodies.Take(6).Should().OnlyContain(b => JsonDocument.Parse(b).RootElement.GetProperty("texts").GetArrayLength() == 8);
        JsonDocument.Parse(handler.Bodies[6]).RootElement.GetProperty("texts").GetArrayLength().Should().Be(2);
        scores.Should().Equal(texts.Select(t => double.Parse(t[1..])));         // position i <-> texts[i]
        JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("query").GetString().Should().Be("q");
        JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("raw_scores").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ScoreAsync_NonSuccessStatus_Throws()
    {
        var handler = new FakeHandler((_, _) => Json("{\"error\":\"boom\"}", HttpStatusCode.InternalServerError));

        var act = () => Client(handler).ScoreAsync("q", ["t1"], CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ScoreAsync_ResponseMissingAnIndex_Throws()
    {
        var handler = new FakeHandler((_, _) => Json("[{\"index\":0,\"score\":1.0}]"));

        var act = () => Client(handler).ScoreAsync("q", ["t1", "t2"], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*2 texts*1 score*");
    }

    [Fact]
    public async Task GetInfoAsync_ReadsModelIdMaxInputLengthAndAutoTruncate()
    {
        var handler = new FakeHandler((_, _) => Json(
            "{\"model_id\":\"cross-encoder/ms-marco-MiniLM-L-6-v2\",\"max_input_length\":512,\"auto_truncate\":true,\"max_batch_requests\":8}"));

        var info = await Client(handler).GetInfoAsync(CancellationToken.None);

        info.Should().Be(new TeiInfo("cross-encoder/ms-marco-MiniLM-L-6-v2", 512, true));
    }
}
