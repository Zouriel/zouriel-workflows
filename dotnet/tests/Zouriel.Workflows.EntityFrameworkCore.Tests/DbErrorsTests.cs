using Microsoft.EntityFrameworkCore;
using Zouriel.Workflows.Engine;

namespace Zouriel.Workflows.EntityFrameworkCore.Tests;

public sealed class DbErrorsTests
{
    /// <summary>Shaped like a provider exception: the code is read from its <c>SqlState</c> property.</summary>
    private sealed class FakeProviderException(string sqlState, string message) : Exception(message)
    {
        public string SqlState { get; } = sqlState;
    }

    [Theory]
    [InlineData("40P01")]
    [InlineData("40001")]
    public void Deadlocks_and_serialization_failures_are_retried(string state)
    {
        var e = new DbUpdateException("save failed", new FakeProviderException(state, "deadlock detected"));
        Assert.IsType<WorkflowConcurrencyException>(DbErrors.ToConcurrency(e));
        Assert.IsType<WorkflowConcurrencyException>(DbErrors.ToConcurrency(new FakeProviderException(state, "from a query")));
    }

    [Fact]
    public void Messages_alone_never_decide()
    {
        // The words a localised server might use mean nothing without a code.
        var e = new DbUpdateException("save failed", new InvalidOperationException("duplicate key value violates unique constraint (23505) PRIMARY KEY"));
        Assert.False(DbErrors.IsUniqueViolation(e));
        Assert.Null(DbErrors.ToConcurrency(e));
    }

    [Fact]
    public void Workflow_refusals_pass_through()
    {
        Assert.Null(DbErrors.ToConcurrency(new WorkflowFieldOwnedException("application", "status", "review")));
    }
}
