namespace ClaimCore.Tests.Architecture.NegativeFixtures

open System
open ClaimCore.Domain
open ClaimCore.Wire

type PostgresReference =
    static member Target() = typeof<ClaimCore.Postgres.IRuntime>

type ClockCall =
    static member Read() = DateTimeOffset.UtcNow


type JsonSerializerCall =
    static member Write() =
        System.Text.Json.JsonSerializer.Serialize("control")


type ApplicationInternalsFromWeb =
    static member InternalsDirect() =
        ClaimCore.Application.LedgerEntries.position Sequence.zero (Array.zeroCreate 32)


type DirectEnumeration =
    static member Read() =
        ClaimCore.Json.Codec.enumeration [ 0, "ZERO" ]
