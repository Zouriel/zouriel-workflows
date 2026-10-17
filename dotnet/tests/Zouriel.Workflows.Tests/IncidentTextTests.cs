using Zouriel.Workflows.Engine;

namespace Zouriel.Workflows.Tests;

/// <summary>How a failure reads in an incident: the real cause, not the wrapper that hides it.</summary>
public sealed class IncidentTextTests
{
    [Fact]
    public void A_wrapped_failure_reads_as_its_innermost_cause()
    {
        var db = new InvalidOperationException("An error occurred while saving the entity changes. See the inner exception for details.",
            new FormatException("23505: duplicate key value violates unique constraint \"ix_invoices_number\""));
        Assert.Equal("FormatException: 23505: duplicate key value violates unique constraint \"ix_invoices_number\"", WorkflowEngine.DescribeError(db));
    }

    [Fact]
    public void A_workflow_refusal_keeps_its_own_message_even_when_wrapped()
    {
        Assert.Equal("You are not allowed.", WorkflowEngine.DescribeError(new WorkflowException("notAllowed", "You are not allowed.")));
        Assert.Equal("You are not allowed.", WorkflowEngine.DescribeError(new InvalidOperationException("outer", new WorkflowException("notAllowed", "You are not allowed."))));
        Assert.Equal("InvalidOperationException: plain", WorkflowEngine.DescribeError(new InvalidOperationException("plain")));
    }
}
