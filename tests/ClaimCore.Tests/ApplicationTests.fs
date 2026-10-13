module ClaimCore.Tests.ApplicationTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreContractTests

let private instant = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)
let private installation = Guid.Parse "90000000-0000-4000-8000-000000000001"

let private owner =
    PrincipalKey.human "https://issuer.example/" "owner"
    |> Result.defaultWith failtest

let private calendar =
    { new IBusinessCalendar with
        member _.TimeZoneId = "Etc/UTC"
        member _.DateAt value = DateOnly.FromDateTime value.UtcDateTime
    }

let private fixture () =
    let store = new MemoryLedgerStore.Store(instant)

    let value: Fixture =
        {
            Store = store :> ILedgerStore
            InstallationId = installation
            Owner = owner
            Calendar = calendar
            Dispose = fun () -> (store :> IDisposable).Dispose()
        }

    store, value

let private run action =
    async {
        let store, value = fixture () in

        try
            do! action store value |> Async.AwaitTask
        finally
            value.Dispose()
    }

let private openRequest n reference =
    request n reference 0L (Command.Open registration)

let private sameRejection = ExecuteOutcome.Rejected Rejection.ResourceUnavailable

let private roleExpectation =
    function
    | Capability.ReadDefinition ->
        [ Role.Owner; Role.CaseReader; Role.CaseEditor; Role.DataSteward; Role.Auditor ]
    | Capability.ListCases
    | Capability.ReadCase
    | Capability.ReadHistorySummary
    | Capability.ObserveOperation ->
        [ Role.CaseReader; Role.CaseEditor; Role.DataSteward; Role.Auditor ]
    | Capability.ReadHistoryFull -> [ Role.CaseEditor; Role.Auditor ]
    | Capability.EditCase -> [ Role.CaseEditor ]
    | Capability.StewardCase -> [ Role.DataSteward ]
    | Capability.ManageAuthority -> [ Role.Owner ]

let private grant (actor: IActorClaimsCore) eventId target role scope active =
    task {
        let! result =
            actor.Authority.SetGrant(eventId, target, role, scope, active, CancellationToken.None)

        applied result
    }

let private checkCombination principal role capability scope resource =
    let actor =
        {
            ActorId = id 1
            Principal = principal
            Enabled = true
            Grants = [ { Role = role; Scope = scope } ]
        }

    let covers =
        match scope, resource with
        | _, ResourceScope.Definition
        | GrantScope.Installation, _ -> true
        | GrantScope.Case a, ResourceScope.Case b -> a = b
        | _ -> false

    let human =
        principal = owner
        || (capability <> Capability.StewardCase && capability <> Capability.ManageAuthority)

    Expect.equal
        (ActorAuthorization.can principal (Some actor) capability resource)
        (List.contains role (roleExpectation capability) && covers && human)
        "Independent role/scope/principal expectation"

let private matrix () =
    let service =
        PrincipalKey.service "https://issuer.example/" "automation"
        |> Result.defaultWith failtest

    let roles =
        [ Role.Owner; Role.CaseReader; Role.CaseEditor; Role.DataSteward; Role.Auditor ]

    let caps =
        [
            Capability.ReadDefinition
            Capability.ListCases
            Capability.ReadCase
            Capability.ReadHistorySummary
            Capability.ReadHistoryFull
            Capability.ObserveOperation
            Capability.EditCase
            Capability.StewardCase
            Capability.ManageAuthority
        ]

    let scopes =
        [ GrantScope.Installation; GrantScope.Case(id 1); GrantScope.Case(id 2) ]

    let resources =
        [
            ResourceScope.Definition
            ResourceScope.Installation
            ResourceScope.Case(id 1)
            ResourceScope.Case(id 2)
        ]

    let mutable count = 0

    for principal in [ owner; service ] do
        for role in roles do
            for capability in caps do
                for scope in scopes do
                    for resource in resources do
                        checkCombination principal role capability scope resource
                        count <- count + 1

    Expect.equal count 1080 "Every combination"

[<Tests>]
let tests =
    testList
        "Application decisions"
        [
            testCase
                "[CC-AUTH-001] five roles and nine capabilities match an independent exhaustive matrix"
                matrix
            testCase
                "[CC-AUTH-001] absent disabled mismatched and ungranted actors are default-deny"
                (fun () ->
                    let baseActor =
                        {
                            ActorId = id 1
                            Principal = owner
                            Enabled = true
                            Grants =
                                [
                                    {
                                        Role = Role.CaseEditor
                                        Scope = GrantScope.Installation
                                    }
                                ]
                        }

                    let other =
                        PrincipalKey.human "https://issuer.example/" "other"
                        |> Result.defaultWith failtest

                    for actor in
                        [
                            None
                            Some { baseActor with Enabled = false }
                            Some { baseActor with Principal = other }
                            Some { baseActor with ActorId = Guid.Empty }
                            Some { baseActor with Grants = [] }
                        ] do
                        Expect.isFalse
                            (ActorAuthorization.can
                                owner
                                actor
                                Capability.EditCase
                                ResourceScope.Installation)
                            "No inferred authority")
            testCaseAsync
                "[CC-APP-002] exact replay is stable and authored amount spelling conflicts"
                (run (fun store fixture ->
                    task {
                        let! actor = editor fixture
                        let original = openRequest 10 "UNIT-001"
                        let! result = actor.Execute(original, CancellationToken.None)
                        let receipt = accepted result
                        let! replay = actor.Execute(original, CancellationToken.None)

                        Expect.equal
                            replay
                            (ExecuteOutcome.AlreadyAccepted receipt)
                            "Original receipt"

                        let! conflict =
                            actor.Execute(
                                { original with
                                    Command =
                                        Command.Open
                                            { registration with
                                                ClaimedAmount = "1000"
                                            }
                                },
                                CancellationToken.None
                            )

                        Expect.equal
                            conflict
                            (ExecuteOutcome.Rejected Rejection.IdempotencyConflict)
                            "Authored spelling is identity"

                        Expect.equal store.Entries.Length 3 "Nothing appended by replay/refusal"
                    }))
            testCaseAsync
                "[CC-APP-001] malformed command and stewardship shape perform no store work"
                (run (fun store fixture ->
                    task {
                        let actor = core fixture owner

                        let! malformed =
                            actor.Execute(
                                { openRequest 10 "UNIT-001" with
                                    OperationId = Guid.Empty
                                },
                                CancellationToken.None
                            )

                        match malformed with
                        | ExecuteOutcome.Rejected(Rejection.Domain _) -> ()
                        | _ -> failtest "Invalid request admitted"

                        let bad: StewardshipRequest =
                            {
                                OperationId = id 11
                                CaseReference = "UNIT-001"
                                ExpectedVersion = 1L
                                Action = StewardshipAction.Void Unchecked.defaultof<string>
                            }

                        let! result = actor.Steward(bad, CancellationToken.None)

                        Expect.equal
                            result
                            (ExecuteOutcome.Rejected Rejection.InvalidReason)
                            "Shape refused before encoding"

                        Expect.equal store.DecisionCalls 0 "No storage callback"
                    }))
            testCaseAsync
                "[CC-APP-001] failure and pre-dispatch cancellation are definite and record nothing"
                (run (fun store fixture ->
                    task {
                        let! actor = editor fixture
                        store.NextFailure <- StoreFailure.Unavailable

                        let! failure =
                            actor.Execute(openRequest 10 "UNIT-001", CancellationToken.None)

                        Expect.equal
                            failure
                            (ExecuteOutcome.Failed CoreFault.StoreUnavailable)
                            "Definite failure"

                        use cancelled = new CancellationTokenSource()
                        cancelled.Cancel()
                        let! result = actor.Execute(openRequest 10 "UNIT-001", cancelled.Token)
                        Expect.equal result ExecuteOutcome.Cancelled "Before dispatch"
                        Expect.equal store.Entries.Length 2 "Neither wrote"
                    }))
            testCaseAsync
                "[CC-APP-002] unknown commit resolves by exact replay whether it landed or not"
                (run (fun store fixture ->
                    task {
                        let! actor = editor fixture

                        for n, lands in [ 10, true; 11, false ] do
                            store.UnknownCommitLands <- lands
                            store.NextFailure <- StoreFailure.CommitOutcomeUnknown
                            let original = openRequest n (if lands then "LANDED" else "NOT-LANDED")
                            let! uncertain = actor.Execute(original, CancellationToken.None)

                            match uncertain with
                            | ExecuteOutcome.OutcomeUnknown(opId, _) ->
                                Expect.equal opId original.OperationId "Original identity"
                            | _ -> failtest "Lost confirmation relabeled"

                            let! result = actor.Execute(original, CancellationToken.None)

                            match result, lands with
                            | ExecuteOutcome.AlreadyAccepted _, true
                            | ExecuteOutcome.Accepted _, false -> ()
                            | _ -> failtest "Replay did not reconcile"

                            Expect.equal
                                (store.Entries
                                 |> List.filter (fun entry ->
                                     entry.OperationId = original.OperationId)
                                 |> List.length)
                                1
                                "Exactly one acceptance"
                    }))
            testCaseAsync
                "admitted acceptance is not relabeled by cancellation after commit"
                (run (fun store fixture ->
                    task {
                        let! actor = editor fixture
                        use cancellation = new CancellationTokenSource()
                        store.AfterCommit <- cancellation.Cancel
                        let! result = actor.Execute(openRequest 10 "UNIT-001", cancellation.Token)
                        accepted result |> ignore

                        Expect.isTrue
                            cancellation.IsCancellationRequested
                            "Cancellation reached the boundary"
                    }))
            testCaseAsync
                "stateless preview remains advice and executes against current state"
                (run (fun store fixture ->
                    task {
                        let! actor = editor fixture
                        let original = openRequest 10 "UNIT-001"
                        let! preview = actor.Preview(original, CancellationToken.None)

                        match preview with
                        | PreviewOutcome.Reviewed(review, _) ->
                            Expect.isTrue review.IsAdvisory "Advice"
                        | _ -> failtest "Preview failed"

                        Expect.equal store.Entries.Length 2 "Preview stores nothing"

                        let! other =
                            actor.Execute(openRequest 11 "UNIT-001", CancellationToken.None)

                        accepted other |> ignore
                        let! result = actor.Execute(original, CancellationToken.None)

                        Expect.equal
                            result
                            (ExecuteOutcome.Rejected(
                                Rejection.Domain(DomainError.VersionConflict 1L)
                            ))
                            "Fresh state rechecked"
                    }))
            testCaseAsync
                "business date comes from the supplied snapshot instant and calendar"
                (run (fun store fixture ->
                    task {
                        let! actor = editor fixture
                        store.Instant <- DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)

                        let! result =
                            actor.Execute(openRequest 10 "UNIT-001", CancellationToken.None)

                        Expect.equal
                            result
                            (ExecuteOutcome.Rejected(
                                Rejection.Domain(
                                    DomainError.InvalidInput(
                                        InputTarget.IncidentNotificationDate,
                                        InputViolation.Date DateViolation.FutureDate
                                    )
                                )
                            ))
                            "Notification is future in the supplied calendar"

                        Expect.equal store.Entries.Length 2 "No future assertion"
                    }))
            testCaseAsync
                "[CC-AUTH-001] lost grant denies exact replay and current case reads"
                (run (fun _ fixture ->
                    task {
                        let! actor = editor fixture
                        let original = openRequest 10 "UNIT-001"
                        let! result = actor.Execute(original, CancellationToken.None)
                        accepted result |> ignore

                        do! grant actor (id 3) owner Role.CaseEditor GrantTarget.Installation false
                        let! replay = actor.Execute(original, CancellationToken.None)
                        Expect.equal replay sameRejection "Rechecked authority"
                        let! view = actor.Get("UNIT-001", CancellationToken.None)

                        Expect.equal
                            view
                            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
                            "No stale facade authority"
                    }))
            testCaseAsync
                "[CC-AUTH-001] absent inaccessible and erased-beyond-view refuse identically"
                (run (fun _ fixture ->
                    task {
                        let! actor = editor fixture

                        do! grant actor (id 3) owner Role.DataSteward GrantTarget.Installation true

                        for n, reference in [ 10, "LIVE"; 11, "ERASED" ] do
                            let! result =
                                actor.Execute(openRequest n reference, CancellationToken.None) in

                            accepted result |> ignore

                        let erase: StewardshipRequest =
                            {
                                OperationId = id 12
                                CaseReference = "ERASED"
                                ExpectedVersion = 1L
                                Action = StewardshipAction.Erase
                            }

                        let! erased = actor.Steward(erase, CancellationToken.None)
                        accepted erased |> ignore

                        do! grant actor (id 4) owner Role.CaseEditor GrantTarget.Installation false

                        do!
                            grant
                                actor
                                (id 5)
                                owner
                                Role.DataSteward
                                GrantTarget.Installation
                                false

                        let! absent = actor.Get("ABSENT", CancellationToken.None)
                        let! inaccessible = actor.Get("LIVE", CancellationToken.None)
                        let! erased = actor.Get("ERASED", CancellationToken.None)
                        Expect.equal absent inaccessible "Absent/inaccessible"
                        Expect.equal erased absent "Erased beyond view"
                    }))
            testCaseAsync
                "[CC-LIFE-001] stewardship advances once blocks work and can reinstate"
                (run (fun _ fixture ->
                    task {
                        let! actor = editor fixture

                        do! grant actor (id 3) owner Role.DataSteward GrantTarget.Installation true

                        let! opened =
                            actor.Execute(openRequest 10 "UNIT-001", CancellationToken.None)

                        accepted opened |> ignore

                        let act n version action : StewardshipRequest =
                            {
                                OperationId = id n
                                CaseReference = "UNIT-001"
                                ExpectedVersion = version
                                Action = action
                            }

                        let! voided =
                            actor.Steward(
                                act 11 1L (StewardshipAction.Void "Wrong reference"),
                                CancellationToken.None
                            )

                        Expect.equal (accepted voided).Revision 2L "One revision"

                        let! blocked =
                            actor.Execute(
                                request 12 "UNIT-001" 2L Command.Close,
                                CancellationToken.None
                            )

                        Expect.equal
                            blocked
                            (ExecuteOutcome.Rejected Rejection.CaseVoided)
                            "Work blocked"

                        let! restored =
                            actor.Steward(
                                act 13 2L (StewardshipAction.Reinstate "Reference confirmed"),
                                CancellationToken.None
                            )

                        Expect.equal (accepted restored).Revision 3L "Reinstatement revision"

                        let! closed =
                            actor.Execute(
                                request 14 "UNIT-001" 3L Command.Close,
                                CancellationToken.None
                            )

                        accepted closed |> ignore
                    }))
            testCaseAsync
                "last enabled human owner cannot be disabled or lose the installation grant"
                (run (fun _ fixture ->
                    task {
                        let! actor = bootstrap fixture

                        let! disable =
                            actor.Authority.SetEnabled(id 2, owner, false, CancellationToken.None)

                        Expect.equal
                            disable
                            (AuthorityOutcome.Rejected Rejection.LastOwner)
                            "Last owner disable"

                        let! remove =
                            actor.Authority.SetGrant(
                                id 3,
                                owner,
                                Role.Owner,
                                GrantTarget.Installation,
                                false,
                                CancellationToken.None
                            )

                        Expect.equal
                            remove
                            (AuthorityOutcome.Rejected Rejection.LastOwner)
                            "Last grant"

                        let other =
                            PrincipalKey.human "https://issuer.example/" "other-owner"
                            |> Result.defaultWith failtest

                        let! registered =
                            actor.Authority.RegisterActor(id 4, other, CancellationToken.None)

                        applied registered

                        let! granted =
                            actor.Authority.SetGrant(
                                id 5,
                                other,
                                Role.Owner,
                                GrantTarget.Installation,
                                true,
                                CancellationToken.None
                            )

                        applied granted

                        let! disabled =
                            actor.Authority.SetEnabled(id 6, owner, false, CancellationToken.None)

                        applied disabled
                    }))
            testCaseAsync
                "authority exact replay observes the original target and conflicts on different content"
                (run (fun store fixture ->
                    task {
                        let! actor = bootstrap fixture

                        let other =
                            PrincipalKey.human "https://issuer.example/" "reader"
                            |> Result.defaultWith failtest

                        let! registered =
                            actor.Authority.RegisterActor(id 2, other, CancellationToken.None)

                        applied registered

                        let! granted =
                            actor.Authority.SetGrant(
                                id 3,
                                other,
                                Role.CaseReader,
                                GrantTarget.Installation,
                                true,
                                CancellationToken.None
                            )

                        applied granted

                        let! replay =
                            actor.Authority.SetGrant(
                                id 3,
                                other,
                                Role.CaseReader,
                                GrantTarget.Installation,
                                true,
                                CancellationToken.None
                            )

                        match replay with
                        | AuthorityOutcome.AlreadyApplied(_, _, target) ->
                            Expect.equal target (id 2) "Original target ID"
                        | _ -> failtest "Replay missing"

                        let! conflict =
                            actor.Authority.SetGrant(
                                id 3,
                                other,
                                Role.CaseReader,
                                GrantTarget.Installation,
                                false,
                                CancellationToken.None
                            )

                        Expect.equal
                            conflict
                            (AuthorityOutcome.Rejected Rejection.IdempotencyConflict)
                            "Conflict"

                        let! observed = actor.Authority.Observe(id 3, CancellationToken.None)
                        Expect.equal observed replay "Operation-bound observation"
                        Expect.equal store.Entries.Length 3 "No extra authority event"
                    }))
            testCaseAsync
                "case-scoped editing cannot open cases and list reapplies visibility"
                (run (fun _ fixture ->
                    task {
                        let! ownerCore = editor fixture

                        let! opened =
                            ownerCore.Execute(openRequest 10 "VISIBLE", CancellationToken.None)

                        accepted opened |> ignore

                        let scoped =
                            PrincipalKey.human "https://issuer.example/" "scoped"
                            |> Result.defaultWith failtest

                        let! registered =
                            ownerCore.Authority.RegisterActor(id 3, scoped, CancellationToken.None)

                        applied registered

                        let! granted =
                            ownerCore.Authority.SetGrant(
                                id 4,
                                scoped,
                                Role.CaseEditor,
                                GrantTarget.CaseReference "VISIBLE",
                                true,
                                CancellationToken.None
                            )

                        applied granted
                        let actor = core fixture scoped

                        let! refused =
                            actor.Execute(openRequest 11 "OTHER", CancellationToken.None)

                        Expect.equal refused sameRejection "Creation requires installation grant"

                        let! page =
                            actor.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)

                        match page with
                        | QueryOutcome.Succeeded page ->
                            Expect.equal page.Items.Length 1 "Visible case"
                            Expect.isEmpty page.AvailableCommands "No OPEN advice"
                        | _ -> failtest "Missing page"

                        let! revoked =
                            ownerCore.Authority.SetGrant(
                                id 5,
                                scoped,
                                Role.CaseEditor,
                                GrantTarget.CaseReference "VISIBLE",
                                false,
                                CancellationToken.None
                            )

                        applied revoked

                        let! denied =
                            actor.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)

                        Expect.equal
                            denied
                            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
                            "Current authority per page"
                    }))
            testCase "malformed keyset cursors refuse without throwing" (fun () ->
                for value in
                    [ Unchecked.defaultof<string>; ""; "!"; "AA"; String.replicate 429 "A" ] do
                    Expect.isError (CaseListCursor.decode value) "Malformed list cursor"

                let token = CaseListCursor.encode "नाम"
                Expect.equal (CaseListCursor.decode token) (Ok "नाम") "Opaque Unicode position"
                Expect.isError (HistoryCursor.decode "!invalid!") "Malformed history cursor")
            testCaseAsync
                "stored identity corruption is a fault and discloses no receipt"
                (run (fun _ fixture ->
                    task {
                        let! actor = editor fixture

                        let! opened =
                            actor.Execute(openRequest 10 "UNIT-001", CancellationToken.None)

                        accepted opened |> ignore

                        let decide (view: CaseLockedView) =
                            let stored = Option.get view.ExistingOperation

                            let invalid =
                                { stored with
                                    RequestSha256 = Some(Array.zeroCreate 32)
                                }

                            match
                                OperationIdentity.decide
                                    fixture.Owner
                                    view.Actor
                                    (RequestRecord.encode (openRequest 10 "UNIT-001"))
                                    (Some invalid)
                            with
                            | IdentityDecision.Fail CoreFault.OperationContentConflict ->
                                CaseDecision.Refuse Rejection.ResourceUnavailable
                            | _ -> failtest "Corrupt identity accepted"

                        let! _ =
                            fixture.Store.WriteCase(
                                fixture.Owner,
                                id 10,
                                "UNIT-001",
                                decide,
                                CancellationToken.None
                            )

                        return ()
                    }))
            testCaseAsync
                "initial-owner provisioning is explicit idempotent and human-only"
                (run (fun store fixture ->
                    task {
                        let! _ = bootstrap fixture

                        let! repeat =
                            fixture.Store.WriteAuthority(
                                owner,
                                id 1,
                                owner,
                                None,
                                AuthorityDecisions.decide
                                    installation
                                    owner
                                    (id 1)
                                    owner
                                    AuthorityAction.ProvisionOwner,
                                CancellationToken.None
                            )

                        match repeat with
                        | Ok(AuthorityWriteResult.Replayed _) -> ()
                        | _ -> failtest "Exact provision repeat failed"

                        Expect.equal store.Entries.Length 1 "No duplicate initial event"

                        let payload =
                            LedgerEntries.payload store.Entries.Head
                            |> Result.defaultWith (fun _ -> failtest "Missing authority payload")

                        let decoded =
                            AuthorityRecord.decode 65536 payload.Request
                            |> Result.defaultWith (fun _ -> failtest "Invalid authority payload")

                        Expect.equal
                            decoded.Action
                            RecordedAuthorityAction.ProvisionOwner
                            "Owner grant is recorded explicitly"

                    }))
            testCaseAsync
                "initial-owner provisioning refuses service principals"
                (run (fun _ fixture ->
                    task {
                        let service =
                            PrincipalKey.service "https://issuer.example/" "service"
                            |> Result.defaultWith failtest

                        let! result =
                            fixture.Store.WriteAuthority(
                                service,
                                id 1,
                                service,
                                None,
                                AuthorityDecisions.decide
                                    installation
                                    service
                                    (id 1)
                                    service
                                    AuthorityAction.ProvisionOwner,
                                CancellationToken.None
                            )

                        match result with
                        | Ok(AuthorityWriteResult.Refused Rejection.HumanPrincipalRequired) -> ()
                        | _ -> failtest "Human owner required"
                    }))
            testCaseAsync
                "a private operation identity does not leak through a readable different case"
                (run (fun _ fixture ->
                    task {
                        let! admin = editor fixture

                        for n, reference in [ 10, "VISIBLE"; 11, "PRIVATE" ] do
                            let! result =
                                admin.Execute(openRequest n reference, CancellationToken.None)

                            accepted result |> ignore

                        let target =
                            PrincipalKey.human "https://issuer.example/" "scoped"
                            |> Result.defaultWith failtest

                        let! registered =
                            admin.Authority.RegisterActor(id 3, target, CancellationToken.None)

                        applied registered

                        let! granted =
                            admin.Authority.SetGrant(
                                id 4,
                                target,
                                Role.CaseEditor,
                                GrantTarget.CaseReference "VISIBLE",
                                true,
                                CancellationToken.None
                            )

                        applied granted
                        let actor = core fixture target

                        let! refused =
                            actor.Execute(
                                request 11 "VISIBLE" 1L Command.Close,
                                CancellationToken.None
                            )

                        Expect.equal
                            refused
                            sameRejection
                            "No identity conflict oracle for private case"
                    }))
            testCaseAsync
                "case and authority observation cannot cross their scope"
                (run (fun _ fixture ->
                    task {
                        let! actor = editor fixture

                        let! result =
                            actor.Execute(openRequest 10 "UNIT-001", CancellationToken.None)

                        accepted result |> ignore
                        let! authorityAsCase = actor.ObserveOperation(id 1, CancellationToken.None)

                        Expect.equal
                            authorityAsCase
                            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
                            "Authority is not a case receipt"

                        let! caseAsAuthority =
                            actor.Authority.Observe(id 10, CancellationToken.None)

                        Expect.equal
                            caseAsAuthority
                            (AuthorityOutcome.Rejected Rejection.ResourceUnavailable)
                            "Case is not authority observation"
                    }))
            testCaseAsync
                "a failed projection publishes neither entry nor projection"
                (run (fun store fixture ->
                    task {
                        let! actor = editor fixture

                        let! result =
                            actor.Execute(openRequest 10 "UNIT-001", CancellationToken.None)

                        accepted result |> ignore
                        let before = store.Entries.Length

                        let decide (view: CaseLockedView) =
                            let draft =
                                LedgerEntries.create
                                    installation
                                    view.Head
                                    view.ObservedAt
                                    view.PayloadNonce
                                    (id 1)
                                    (id 99)
                                    (StoredScope.Case(
                                        id 99,
                                        1L,
                                        AcceptedAct.Stewardship StewardshipKind.Erase,
                                        DateOnly(2026, 9, 7)
                                    ))
                                    [| 1uy |]
                                    [||]

                            CaseDecision.Append(draft, CaseProjection.Erase(id 99, 1L))

                        let! refused =
                            fixture.Store.WriteCase(
                                owner,
                                id 99,
                                "MISSING",
                                decide,
                                CancellationToken.None
                            )

                        match refused with
                        | Error StoreFailure.Integrity -> ()
                        | _ -> failtest "Invalid projection did not fail"

                        Expect.equal store.Entries.Length before "No orphan append"
                        Expect.equal store.Cases.Length 1 "No projection change"
                    }))
            testCaseAsync
                "post-commit interruption remains unknown and exact replay is accepted"
                (run (fun store fixture ->
                    task {
                        let! actor = editor fixture
                        store.AfterCommit <- fun () -> raise (OperationCanceledException())
                        let original = openRequest 10 "UNIT-001"
                        let! result = actor.Execute(original, CancellationToken.None)

                        match result with
                        | ExecuteOutcome.OutcomeUnknown _ -> ()
                        | _ -> failtest "Post-commit interruption relabeled"

                        store.AfterCommit <- ignore
                        let! replay = actor.Execute(original, CancellationToken.None)

                        match replay with
                        | ExecuteOutcome.AlreadyAccepted _ -> ()
                        | _ -> failtest "Committed identity not replayed"
                    }))
            testCase "principal identity cannot contain malformed Unicode or NUL" (fun () ->
                for value in [ String([| char 0xD800 |]); "subject\u0000value" ] do
                    Expect.isError
                        (PrincipalKey.human "https://issuer.example/" value)
                        "Lossless persisted identity required"

                    Expect.isError
                        (PrincipalKey.service "https://issuer.example/" value)
                        "Lossless service identity required")
            testCaseAsync
                "exact replay precedes later case revisions and preview also observes acceptance"
                (run (fun _ fixture ->
                    task {
                        let! actor = editor fixture
                        let original = openRequest 10 "UNIT-001"
                        let! first = actor.Execute(original, CancellationToken.None)
                        let receipt = accepted first

                        let! closed =
                            actor.Execute(
                                request 11 "UNIT-001" 1L Command.Close,
                                CancellationToken.None
                            )

                        accepted closed |> ignore
                        let! replay = actor.Execute(original, CancellationToken.None)

                        Expect.equal
                            replay
                            (ExecuteOutcome.AlreadyAccepted receipt)
                            "Original revision returned"

                        let! preview = actor.Preview(original, CancellationToken.None)

                        Expect.equal
                            preview
                            (PreviewOutcome.AlreadyAccepted receipt)
                            "No fresh preview for accepted identity"
                    }))
            ClaimCore.TestSupport.StoreContractTests.tests (fun () ->
                let _, value = fixture () in Task.FromResult value)
        ]
