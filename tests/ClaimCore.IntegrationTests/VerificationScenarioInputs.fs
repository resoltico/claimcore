module ClaimCore.IntegrationTests.VerificationScenarioInputs

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport


let internal livePayload (value: LedgerRow) =
    let bytes =
        value.Entry.Payload
        |> Option.defaultWith (fun () -> failtest "Retained case fixture is required")

    let frame =
        LedgerEntry.decodePayload 262144 bytes
        |> Result.defaultWith (fun _ -> failtest "Invalid fixture frame")

    let request =
        RequestRecord.decode 65536 frame.Request
        |> Result.defaultWith (fun _ -> failtest "Invalid fixture request")
        |> _.Authored

    let snapshot =
        ClaimCore.RecordFormat.CaseRecord.decodeSnapshot frame.Snapshot
        |> Result.defaultWith (fun _ -> failtest "Invalid fixture snapshot")

    request, snapshot

let internal updateCase request snapshot value =
    payload
        (CaseFixtures.encode request)
        (ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot)
        value

let internal updateAuthority request value =
    payload (AuthorityRecord.encode request) Array.empty value

let internal target subject =
    PrincipalKey.human "https://issuer.example/" subject
    |> Result.defaultWith (string >> failtest)

let internal register (actor: ServiceContext) n principal =
    task {
        let! outcome =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id n) principal AuthorityAct.Register))
                (token))

        acceptedAuthority outcome
    }
