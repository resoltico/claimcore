namespace ClaimCore.Tests.Architecture.NegativeFixtures

open System
open ClaimCore.Domain
open ClaimCore.Wire


type ClockCall =
    static member Read() = DateTimeOffset.UtcNow


type JsonSerializerCall =
    static member Write() =
        System.Text.Json.JsonSerializer.Serialize("control")



type DirectEnumeration =
    static member Read() =
        ClaimCore.Json.Codec.enumeration [ 0, "ZERO" ]
