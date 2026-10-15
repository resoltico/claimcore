module ClaimCore.Tests.CursorTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Tests.Fixtures

let private key = [| 0uy .. 31uy |]
let private installation = Guid.Parse "90000000-0000-4000-8000-000000000001"
let private instant = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)

let private principal =
    PrincipalKey.human "https://issuer.example/" "reader" |> accepted

let private actor: ActorAuthority =
    {
        ActorId = Guid.Parse "a0000000-0000-4000-8000-000000000001"
        Principal = principal
        Enabled = true
        Grants =
            [
                {
                    Role = Role.CaseHandler
                    Scope = GrantScope.Installation
                }
                {
                    Role = Role.Auditor
                    Scope = GrantScope.Case(Guid.Parse "c0000000-0000-4000-8000-000000000001")
                }
            ]
    }

let private query = CursorQuery.History("Demo-1", HistoryDetail.Full)

let private token () =
    Cursors.encode key installation actor query instant "42"

let private decode current query time token =
    Cursors.decode key installation current query time token

let private lifetime () =
    let value = token ()

    for time in [ instant; instant.AddTicks(TimeSpan.FromMinutes(15.0).Ticks - 1L) ] do
        Expect.equal (decode actor query time value) (Ok "42") "Valid lifetime"

    for time in [ instant.AddTicks(-1L); instant.AddMinutes(15.0); instant.AddDays(1.0) ] do
        Expect.equal
            (decode actor query time value)
            (Error Rejection.InvalidCursor)
            "Future-issued or expired cursor refuses"

    let maximum = DateTimeOffset.MaxValue
    let last = Cursors.encode key installation actor query maximum "42"

    Expect.equal
        (decode actor query maximum last)
        (Ok "42")
        "Expiry arithmetic does not overflow at the calendar bound"

let private bindings () =
    let value = token ()
    let other = PrincipalKey.human "https://issuer.example/" "other" |> accepted

    let variants =
        [
            { actor with
                ActorId = Guid.Parse "a0000000-0000-4000-8000-000000000002"
            }
            { actor with Principal = other }
            { actor with Enabled = false }
            { actor with Grants = [] }
            { actor with
                Grants =
                    {
                        Role = Role.CaseReader
                        Scope = GrantScope.Case(Guid.Parse "c0000000-0000-4000-8000-000000000002")
                    }
                    :: actor.Grants
            }
        ]

    for changed in variants do
        Expect.equal
            (decode changed query instant value)
            (Error Rejection.InvalidCursor)
            "Full authority identity is bound"

    for changed in
        [
            CursorQuery.Cases
            CursorQuery.Actors
            CursorQuery.History("Demo-2", HistoryDetail.Full)
            CursorQuery.History("Demo-1", HistoryDetail.Summary)
        ] do
        Expect.equal
            (decode actor changed instant value)
            (Error Rejection.InvalidCursor)
            "Endpoint, resource, detail and order context is bound"

    Expect.equal
        (decode actor (CursorQuery.History("DEMO-1", HistoryDetail.Full)) instant value)
        (Ok "42")
        "Resource references normalize by ASCII identity"

    let anotherInstallation = Guid.Parse "90000000-0000-4000-8000-000000000002"

    Expect.equal
        (Cursors.decode key anotherInstallation actor query instant value)
        (Error Rejection.InvalidCursor)
        "Installation is bound"

    Expect.equal
        (Cursors.decode (Array.rev key) installation actor query instant value)
        (Error Rejection.InvalidCursor)
        "Runtime restart invalidates prior cursors"

let private grantSet () =
    let value = token ()

    let reordered =
        { actor with
            Grants = List.rev actor.Grants @ actor.Grants
        }

    Expect.equal
        (Cursors.encode key installation reordered query instant "42")
        value
        "Sorted deduplicated full grants have one binding"

    Expect.equal
        (decode reordered query instant value)
        (Ok "42")
        "Ordering and duplicates do not change authority"

    let disabled = { actor with Enabled = false }

    Expect.isFalse
        (ActorAuthorization.can
            principal
            (Some disabled)
            Capability.ReadHistoryFull
            (ResourceScope.Case(Guid.Parse "c0000000-0000-4000-8000-000000000001")))
        "Tokens grant no read rights"

    for malformed in [ ""; value + "="; String.replicate 513 "A"; "not.a.cursor" ] do
        Expect.equal
            (decode actor query instant malformed)
            (Error Rejection.InvalidCursor)
            "Malformed envelope refuses"

[<Tests>]
let tests =
    testList
        "snapshot-bound cursors"
        [
            testCase
                "issued time and the exact fifteen-minute lifetime use the supplied database instant"
                lifetime
            testCase "principal, actor, all grants, query and installation bind the cursor" bindings
            testCase "grant-set normalization is stable and cursors confer no rights" grantSet
        ]
