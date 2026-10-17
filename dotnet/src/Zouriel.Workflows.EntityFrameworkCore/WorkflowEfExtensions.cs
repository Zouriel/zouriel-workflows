using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zouriel.Workflows.DependencyInjection;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Registry;

namespace Zouriel.Workflows.EntityFrameworkCore;

/// <summary>Wires workflows to Entity Framework Core.</summary>
/// <remarks>
/// The engine opens its own transaction for each unit of work. A DbContext configured with a retrying
/// execution strategy (<c>EnableRetryOnFailure</c>) refuses user-started transactions, so the context the
/// workflow store uses must not retry on its own; the engine retries units itself on conflicts.
/// </remarks>
public static class WorkflowEfExtensions
{
    /// <summary>
    /// Stores workflows in <typeparamref name="TContext"/> (call <c>modelBuilder.AddWorkflows()</c> in its
    /// <c>OnModelCreating</c>) and watches its SaveChanges for record changes (add
    /// <c>options.AddWorkflowInterceptor(sp)</c> where the context is configured).
    /// </summary>
    public static WorkflowBuilder UseEntityFrameworkCore<TContext>(this WorkflowBuilder builder) where TContext : DbContext
    {
        builder.Services.AddSingleton(sp => new WorkflowSaveChangesInterceptor(sp.GetRequiredService<WorkflowEngine>()));
        return builder.UseStore((sp, engine) => new EfWorkflowStore<TContext>(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<WorkflowRegistry>()));
    }

    /// <summary>Adds the workflow change interceptor to a DbContext's options.</summary>
    public static DbContextOptionsBuilder AddWorkflowInterceptor(this DbContextOptionsBuilder options, IServiceProvider services) =>
        options.AddInterceptors(services.GetRequiredService<WorkflowSaveChangesInterceptor>());
}
