using Ecommerce.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Observability, Entra authentication, health checks, HTTP resilience.
builder.AddServiceDefaults();

var app = builder.Build();

app.UseServiceDefaults();

// Endpoints are added in the phase that implements this service.
app.MapGet("/", () => Results.Ok(new { service = "UserService", status = "scaffolded" }))
   .AllowAnonymous();

app.Run();

/// <summary>Exposed so integration tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
