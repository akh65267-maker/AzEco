using System.Runtime.InteropServices;

namespace UserService.Tests.Integration;

/// <summary>
/// A fact that skips itself when no container runtime is reachable.
///
/// The integration tests need a real PostgreSQL, because the bugs worth catching —
/// unique-violation races, partial index filters, provider translation, citext
/// behaviour — are precisely the ones an in-memory provider cannot reproduce. But a
/// developer without Docker running should get a green suite and a clear skip reason,
/// not a wall of red that trains them to ignore test failures.
///
/// CI does have Docker, so these run there. That asymmetry is deliberate and is why the
/// skip reason names it.
/// </summary>
public sealed class DockerRequiredFactAttribute : FactAttribute
{
    public DockerRequiredFactAttribute()
    {
        if (!DockerProbe.IsAvailable)
        {
            Skip = "No container runtime detected. These tests run in CI, where Docker is available.";
        }
    }
}

/// <summary>
/// Same, for theories.
/// </summary>
public sealed class DockerRequiredTheoryAttribute : TheoryAttribute
{
    public DockerRequiredTheoryAttribute()
    {
        if (!DockerProbe.IsAvailable)
        {
            Skip = "No container runtime detected. These tests run in CI, where Docker is available.";
        }
    }
}

internal static class DockerProbe
{
    // Probed once per process. Checking the socket rather than shelling out to `docker`
    // keeps this fast enough to run in an attribute constructor, which xunit evaluates
    // during discovery for every test in the class.
    public static readonly bool IsAvailable = Probe();

    private static bool Probe()
    {
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 })
        {
            return true;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Directory.Exists(@"\\.\pipe\") && File.Exists(@"\\.\pipe\docker_engine")
            : File.Exists("/var/run/docker.sock");
    }
}
