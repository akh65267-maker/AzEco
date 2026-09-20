using System.Reflection;

namespace OrderService.Tests;

/// <summary>
/// Layering is only real if something enforces it. Project references are easy to add
/// in a hurry and nobody notices in review; these tests fail the build instead.
///
/// The allowed direction is: Api -> Infrastructure -> Application -> Domain.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(OrderService.Domain.AssemblyMarker).Assembly;
    private static readonly Assembly Application = typeof(OrderService.Application.AssemblyMarker).Assembly;

    [Fact]
    public void Domain_does_not_depend_on_any_other_layer()
    {
        var referenced = Domain.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.DoesNotContain("OrderService.Application", referenced);
        Assert.DoesNotContain("OrderService.Infrastructure", referenced);
        Assert.DoesNotContain("OrderService.Api", referenced);
    }

    [Fact]
    public void Domain_does_not_depend_on_infrastructure_technology()
    {
        var referenced = Domain.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

        // The domain must not know about EF Core, Npgsql, Azure SDKs or ASP.NET.
        // Once it does, the business rules can only be tested by standing up a database.
        Assert.DoesNotContain(referenced, name =>
            name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || name.StartsWith("Npgsql", StringComparison.Ordinal)
            || name.StartsWith("Azure.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure()
    {
        var referenced = Application.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.DoesNotContain("OrderService.Infrastructure", referenced);
        Assert.DoesNotContain("OrderService.Api", referenced);
    }
}
