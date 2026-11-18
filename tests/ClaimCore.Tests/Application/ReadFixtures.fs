module internal ClaimCore.Tests.ReadFixtures

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

let instant = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)
let id number = OperationIds.numberedAt instant number
let installation = id 500

let owner =
    PrincipalKey.human "https://issuer.example/" "owner"
    |> Result.defaultWith (string >> failtest)

let other =
    PrincipalKey.human "https://issuer.example/" "other"
    |> Result.defaultWith (string >> failtest)

let service =
    PrincipalKey.service "https://issuer.example/" "service"
    |> Result.defaultWith (string >> failtest)

let calendar =
    { new IBusinessCalendar with
        member _.TimeZoneId = "Etc/UTC"
        member _.DateAt value = DateOnly.FromDateTime value.UtcDateTime
    }

let private decisionStore =
    { new IStore with
        member _.Context _ =
            failtest "Pure decisions must not load requester context"

        member _.Read _ =
            failtest "Pure decisions must not read storage"

        member _.Write _ =
            failtest "Pure decisions must not write storage"
    }

let services installation calendar principal =
    Service.create "decision-tests" installation decisionStore calendar principal

let actor principal actorId roles cases : AccessView =
    {
        Actor =
            Some
                {
                    ActorId = actorId
                    Principal = principal
                    Enabled = true
                    Profile = None
                }
        Installation = roles
        Cases = Map.ofList cases
        HasScopedCases = not cases.IsEmpty
        HasAssignedRoles = not roles.IsEmpty || not cases.IsEmpty
    }

let worker =
    actor
        owner
        ((CaseFixtures.actorId (id 1)))
        [ Role.Owner; Role.CaseHandler; Role.PaymentRecorder; Role.DataSteward ]
        []


let current () =
    let snapshot = ResponseExamples.snapshot

    {
        StoredCase.CaseId = snapshot.CaseId
        Snapshot = snapshot
        SnapshotBytes = CaseRecord.encodeSnapshot snapshot
        Index = CaseIndex.create instant snapshot None [] []
    }
