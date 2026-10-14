namespace ClaimCore.ArchitectureTests.NegativeFixtures

open System

type PostgresReference =
    static member Target() = typeof<ClaimCore.Postgres.IRuntime>

type HostingReference =
    static member Target() = typeof<ClaimCore.Hosting.IRuntime>

type ClockCall =
    static member Read() = DateTimeOffset.UtcNow

type FileCall =
    static member Read(path) = IO.File.ReadAllText(path)

type EnvironmentCall =
    static member Read() =
        Environment.GetEnvironmentVariable("ARCHITECTURE_NEGATIVE_CONTROL")

type RandomCall =
    static member Read() = Guid.NewGuid()

type NetworkCall =
    static member Read() = new Net.Http.HttpClient()

type TimeZoneCall =
    static member Read() =
        TimeZoneInfo.FindSystemTimeZoneById("Etc/UTC")
