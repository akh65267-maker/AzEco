using Ecommerce.ServiceDefaults;
using UserService.Api;
using UserService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Observability, Entra authentication, health checks, HTTP resilience.
builder.AddServiceDefaults();

// PostgreSQL: local connection string, or Entra token via managed identity in Azure.
builder.AddUserInfrastructure();

// Injected rather than DateTimeOffset.UtcNow so time is controllable in tests.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CurrentUserProvider>();

builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseServiceDefaults();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapUserEndpoints();

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
