namespace ClaimCore.Tests.Architecture.NegativeFixtures

open System
open ClaimCore.Domain
open ClaimCore.Wire

type PostgresReference =
    static member Target() = typeof<ClaimCore.Postgres.IRuntime>

type ClockCall =
    static member Read() = DateTimeOffset.UtcNow

type WireCodecCall =
    static member Read() =
        ClaimCore.Wire.RequestCodecs.caseRequest.Schema

type ProtocolFromDomain =
    static member ProtocolDirect() = ReplyCodecs.execute.Schema

type JsonSerializerCall =
    static member Write() =
        System.Text.Json.JsonSerializer.Serialize("control")

type JsonFromDomain =
    static member JsonDirect() = ClaimCore.Json.JsonValue.Null

type ApplicationInternalsFromWeb =
    static member InternalsDirect() =
        ClaimCore.Application.LedgerEntries.position Sequence.zero (Array.zeroCreate 32)

type RecordFormatFromDomain =
    static member RecordDirect() =
        typeof<ClaimCore.RecordFormat.DecodeProblem>

type DirectEnumeration =
    static member Read() =
        ClaimCore.Json.Codec.enumeration [ 0, "ZERO" ]
