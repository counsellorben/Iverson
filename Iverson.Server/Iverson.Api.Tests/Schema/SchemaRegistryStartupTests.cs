using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Api.Tests.Schema;

/// <summary>
/// CSR round 12 §6.3. <see cref="SchemaRegistry.LoadAsync"/> runs in the api at startup and every
/// 30 s, and the api's login may not run DDL, so the load no longer creates its own table: the
/// worker does that once, at startup. And because the api can start before the worker, its first
/// load may fail; <see cref="SchemaRegistry.HasLoaded"/> is what /health waits for.
/// </summary>
public class SchemaRegistryStartupTests
{
    private readonly ISchemaRegistryRepository _repository = Substitute.For<ISchemaRegistryRepository>();
    private readonly SchemaRegistry _sut;

    public SchemaRegistryStartupTests() =>
        _sut = new SchemaRegistry(_repository, NullLogger<SchemaRegistry>.Instance);

    [Fact]
    public async Task LoadAsync_RunsNoDdl()
    {
        _repository.LoadAllAsync().Returns(Enumerable.Empty<(string TypeName, string SchemaJson)>());

        await _sut.LoadAsync();

        await _repository.DidNotReceive().EnsureTableAsync();
        await _repository.Received(1).LoadAllAsync();
    }

    [Fact]
    public void HasLoaded_IsFalseBeforeAnyLoad() =>
        _sut.HasLoaded.Should().BeFalse();

    [Fact]
    public async Task HasLoaded_StaysFalseWhileLoadsFail_AndTurnsTrueOnTheFirstSuccess()
    {
        _repository.LoadAllAsync()
            .ThrowsAsync(new InvalidOperationException("relation \"_iverson_schema\" does not exist"));

        var failingLoad = () => _sut.LoadAsync();
        await failingLoad.Should().ThrowAsync<InvalidOperationException>();
        _sut.HasLoaded.Should().BeFalse();

        _repository.LoadAllAsync().Returns(Enumerable.Empty<(string TypeName, string SchemaJson)>());
        await _sut.LoadAsync();

        _sut.HasLoaded.Should().BeTrue();
    }
}
