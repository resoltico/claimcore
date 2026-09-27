module ClaimCore.Tests.CaseListCursorTests

open System
open System.Collections.Generic
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Tests.Fixtures

let private principal subject =
    PrincipalKey.human "https://issuer.example.test/realms/cursor" subject
    |> Result.defaultWith (fun _ -> failtest "Synthetic principal is valid.")

let private binding =
    {
        Principal = principal "first"
        ActorId = Guid.Parse("40000000-0000-4000-8000-000000000001")
        GrantRevision = 7L
    }

let private protection () =
    let entries = Dictionary<string, byte array>()

    { new ICaseListCursorProtection with
        member _.Seal(bytes) =
            let token = Guid.NewGuid().ToString("N")
            entries[token] <- Array.copy bytes
            token

        member _.Open(token) =
            match entries.TryGetValue token with
            | true, bytes -> Some(Array.copy bytes)
            | _ -> None
    }

let private boundPayload () =
    let codec = protection ()
    let instant = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)

    let token =
        CaseListCursorCodec.encode codec binding 7L 2 instant "SYNTHETIC-CASE-001"

    let decode actor revision limit now value =
        CaseListCursorCodec.decode codec actor revision limit now value

    Expect.equal (decode binding 7L 2 instant token) (Ok "SYNTHETIC-CASE-001") "Exact query"

    Expect.equal
        (decode
            { binding with
                Principal = principal "other"
            }
            7L
            2
            instant
            token)
        (Error())
        "Principal"

    Expect.equal (decode binding 8L 2 instant token) (Error()) "Authority revision"
    Expect.equal (decode binding 7L 3 instant token) (Error()) "Page query"
    Expect.equal (decode binding 7L 2 (instant.AddMinutes 15.) token) (Error()) "Expiry"
    Expect.equal (decode binding 7L 2 instant "not-a-token") (Error()) "Unknown token"

let private typedRefusal () =
    let store = new CoreStore.Store()
    let recovery = new CoreRecoveryStore.Store()
    recovery.AttachClaimStore(store :> IClaimStore)

    let core =
        ActorCoreFixture.create
            (store :> IClaimStore)
            (recovery :> IRecoveryStore)
            (businessTime today)

    match
        core
            .List(
                {
                    AfterCursor = Some "not-a-token"
                    Limit = 1
                },
                CancellationToken.None
            )
            .Result
    with
    | QueryOutcome.Rejected Rejection.InvalidCaseListCursor as result ->
        let reason =
            match result with
            | QueryOutcome.Rejected value -> value
            | _ -> failtest "Typed rejection disappeared."

        Expect.equal reason.Field (Some "cursor") "One safe field"

        Expect.equal
            (reason
             |> RejectionDiagnostics.describe
             |> RejectionDiagnostics.identifier
             |> RejectionDiagnosticIds.token)
            "QUERY_CASE_LIST_CURSOR_INVALID"
            "One safe diagnostic for every invalid cursor"
    | _ -> failtest "A forged cursor must be a typed refusal."

let tests =
    testList
        "case-list cursor contract"
        [
            testCase "[CC-AUTH-001] list cursor binds caller grant query and expiry" boundPayload
            testCase "[CC-AUTH-001] invalid list cursor has one typed diagnostic" typedRefusal
        ]
