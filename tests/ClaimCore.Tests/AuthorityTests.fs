module ClaimCore.Tests.AuthorityTests

open ClaimCore.Contract

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.Tests.ApplicationFixtures

let private human subject =
    PrincipalKey.human "https://issuer.example/" subject
    |> Result.defaultWith failtest

let private service subject =
    PrincipalKey.service "https://issuer.example/" subject
    |> Result.defaultWith failtest

let private execute (actor: IActorClaimsCore) n target act =
    actor.ExecuteAuthority(authorityRequest (id n) target act, CancellationToken.None)

let private register actor n target =
    task {
        let! result = execute actor n target AuthorityAct.RegisterActor
        acceptedAuthority result
    }

let private lastOwner (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = bootstrap fixture
        let! disabled = execute actor 2 owner (AuthorityAct.SetEnabled false)

        Expect.equal
            disabled
            (ExecuteOutcome.Rejected Rejection.LastOwner)
            "Last owner disable refuses"

        let! removed = grant actor (id 3) owner Role.Owner GrantTarget.Installation false

        Expect.equal
            removed
            (ExecuteOutcome.Rejected Rejection.LastOwner)
            "Last installation grant removal refuses"

        let other = human "other-owner"
        do! register actor 4 other
        let! granted = grant actor (id 5) other Role.Owner GrantTarget.Installation true
        acceptedAuthority granted
        let! disabled = execute actor 6 owner (AuthorityAct.SetEnabled false)
        acceptedAuthority disabled
        let! context = actor.Context CancellationToken.None

        Expect.equal
            context
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "Disabled owner has no cached authority"
    }

let private exactReplay (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = bootstrap fixture
        let target = human "reader"
        do! register actor 2 target

        let! granted =
            grant actor (id 3) target Role.CaseReader GrantTarget.Installation true

        let receipt = accepted granted
        let! replay = grant actor (id 3) target Role.CaseReader GrantTarget.Installation true

        Expect.equal
            replay
            (ExecuteOutcome.AlreadyAccepted receipt)
            "Original accepted authority receipt"

        match receipt.Scope with
        | ReceiptScope.Authority value ->
            Expect.equal value.TargetActorId (id 2) "Target is the registration operation ID"
        | _ -> failtest "Wrong receipt scope"

        Expect.equal receipt.RecordedBy (id 1) "Executor remains the existing owner"

        let! conflict =
            grant actor (id 3) target Role.CaseReader GrantTarget.Installation false

        Expect.equal
            conflict
            (ExecuteOutcome.Rejected Rejection.IdempotencyConflict)
            "Different content conflicts"

        let! observed = actor.Observe(id 3, CancellationToken.None)

        Expect.equal
            observed
            (QueryOutcome.Succeeded receipt)
            "Unified observation returns the same metadata"

        Expect.equal store.Entries.Length 3 "No extra authority operation"
    }

let private provisionReplay (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! _ = bootstrap fixture

        let! repeated =
            fixture.Store.WriteAuthority(
                owner,
                id 1,
                owner,
                None,
                AuthorityDecisions.provision installation (id 1) owner,
                CancellationToken.None
            )

        match repeated with
        | Ok(LedgerWriteResult.Replayed _) -> ()
        | _ -> failtest "Exact owner provisioning did not replay"

        Expect.equal store.Entries.Length 1 "No duplicate bootstrap"

        let payload =
            LedgerEntries.payload store.Entries.Head
            |> Result.defaultWith (fun _ -> failtest "Missing authority payload")

        let decoded =
            AuthorityRecord.decode 65536 payload.Request
            |> Result.defaultWith (fun _ -> failtest "Invalid authority record")

        Expect.equal
            decoded.Act
            RecordedAuthorityAct.ProvisionOwner
            "Provisioning is explicit in the ledger"

        Expect.equal decoded.OperationId (id 1) "Stable operation ID"
    }

let private serviceBootstrap (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let target = service "service"

        let! result =
            fixture.Store.WriteAuthority(
                target,
                id 1,
                target,
                None,
                AuthorityDecisions.provision installation (id 1) target,
                CancellationToken.None
            )

        Expect.equal
            result
            (Ok(LedgerWriteResult.Refused Rejection.HumanPrincipalRequired))
            "Bootstrap is human-only"

        Expect.isEmpty store.Entries "Refusal records nothing"
        Expect.isEmpty store.Actors "No projection-only owner"
    }

let private humanGrantTargets (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let target = service "automation"
        do! register actor 3 target
        let! opened = actor.Execute(openRequest 10 "SCOPED", CancellationToken.None)
        accepted opened |> ignore
        let before = store.Entries.Length
        let mutable n = 20

        for role in [ Role.Owner; Role.DataSteward ] do
            for scope in [ GrantTarget.Installation; GrantTarget.CaseReference "scoped" ] do
                for active in [ false; true ] do
                    let! outcome = grant actor (id n) target role scope active
                    n <- n + 1

                    Expect.equal
                        outcome
                        (ExecuteOutcome.Rejected Rejection.HumanPrincipalRequired)
                        "Locked grant-target rule"

        Expect.equal store.Entries.Length before "Invalid grants append nothing"
        let! allowed = grant actor (id 40) target Role.Auditor GrantTarget.Installation true
        acceptedAuthority allowed

        let! review =
            (core fixture target)
                .ListActors({ AfterCursor = None; Limit = 50 }, CancellationToken.None)

        match review with
        | QueryOutcome.Succeeded _ -> ()
        | _ -> failtest "Registered service auditor should review authority"
    }

let private uncertainty (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = bootstrap fixture

        for n, lands in [ 10, true; 11, false ] do
            let target = human (if lands then "landed" else "not-landed")
            let request = authorityRequest (id n) target AuthorityAct.RegisterActor
            store.UnknownCommitLands <- lands
            store.NextFailure <- StoreFailure.CommitOutcomeUnknown
            let! outcome = actor.ExecuteAuthority(request, CancellationToken.None)

            match outcome with
            | ExecuteOutcome.OutcomeUnknown operationId ->
                Expect.equal operationId request.OperationId "Original authority identity"

            | _ -> failtest "Authority uncertainty relabeled"

            let! retry = actor.ExecuteAuthority(request, CancellationToken.None)

            match retry, lands with
            | ExecuteOutcome.AlreadyAccepted _, true
            | ExecuteOutcome.Accepted _, false -> ()
            | _ -> failtest "Exact authority retry failed"

            Expect.equal
                (store.Entries
                 |> List.filter (fun entry -> entry.OperationId = request.OperationId)
                 |> List.length)
                1
                "One acceptance"
    }

let private failureAndCancellation (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = bootstrap fixture
        let request = authorityRequest (id 10) (human "reader") AuthorityAct.RegisterActor
        store.NextFailure <- StoreFailure.Unavailable
        let! failure = actor.ExecuteAuthority(request, CancellationToken.None)

        Expect.equal
            failure
            (ExecuteOutcome.Failed CoreFault.StoreUnavailable)
            "Definite authority failure"

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        let! cancelled = actor.ExecuteAuthority(request, cancellation.Token)
        Expect.equal cancelled ExecuteOutcome.Cancelled "Before dispatch"
        Expect.equal store.Entries.Length 1 "No authority acceptance"
    }

let private contextAndReview (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = bootstrap fixture
        let target = human "ungranted"
        do! register actor 2 target
        let ungranted = core fixture target
        let! context = ungranted.Context CancellationToken.None

        match context with
        | QueryOutcome.Succeeded value ->
            Expect.isEmpty value.Capabilities "Enabled actor may read context without grants"
        | _ -> failtest "Missing enabled-actor context"

        let! denied =
            ungranted.ListActors({ AfterCursor = None; Limit = 50 }, CancellationToken.None)

        Expect.equal
            denied
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "Authority remains default deny"

        let! missing = (core fixture (human "absent")).Context CancellationToken.None

        Expect.equal
            missing
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "Unregistered principal has no context"

        let! review =
            actor.ListActors({ AfterCursor = None; Limit = 50 }, CancellationToken.None)

        match review with
        | QueryOutcome.Succeeded page ->
            let found = page.Items |> List.find (fun value -> value.ActorId = id 2)

            Expect.equal
                found.Principal
                target
                "RecordedBy attribution includes principal and actor ID"

            Expect.isTrue found.Enabled "Current enabled state"

            let! grants =
                actor.ListGrants(
                    {
                        ActorId = found.ActorId
                        AfterCursor = None
                        Limit = 50
                    },
                    CancellationToken.None
                )

            match grants with
            | QueryOutcome.Succeeded value -> Expect.isEmpty value.Items "No implied grants"
            | _ -> failtest "Expected grant review"
        | _ -> failtest "Owner review unavailable"
    }

let private observationRights (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let! opened = actor.Execute(openRequest 10 "UNIT-001", CancellationToken.None)
        let receipt = accepted opened
        let! authority = actor.Observe(id 1, CancellationToken.None)

        match authority with
        | QueryOutcome.Succeeded { Scope = ReceiptScope.Authority _ } -> ()
        | _ -> failtest "Unified authority observation failed"

        let! observed = actor.Observe(id 10, CancellationToken.None)
        Expect.equal observed (QueryOutcome.Succeeded receipt) "Unified case observation"
        let reader = human "reader"
        do! register actor 3 reader

        let! granted =
            grant actor (id 4) reader Role.CaseReader GrantTarget.Installation true

        acceptedAuthority granted
        let! denied = (core fixture reader).Observe(id 1, CancellationToken.None)

        Expect.equal
            denied
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "Case rights do not grant installation authority review"

        let auditor = human "case-auditor"
        do! register actor 5 auditor

        let! granted =
            grant actor (id 6) auditor Role.Auditor (GrantTarget.CaseReference "UNIT-001") true

        acceptedAuthority granted
        let scoped = core fixture auditor

        let! list =
            scoped.ListActors({ AfterCursor = None; Limit = 50 }, CancellationToken.None)

        Expect.equal
            list
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "Case auditor cannot enumerate installation"

        let! denied = scoped.Observe(id 1, CancellationToken.None)

        Expect.equal
            denied
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "Authority observation requires installation ReadAuthority"
    }

let private actorContext (actor: IActorClaimsCore) =
    task {
        let! context = actor.Context CancellationToken.None

        match context with
        | QueryOutcome.Succeeded value -> return value
        | _ -> return failtest "Expected enabled actor context"
    }

let private contextOffers (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = bootstrap fixture
        let! context = actorContext admin
        let offers = context.Offers
        Expect.equal context.Views [ View.Authority; View.Operations ] "Owner views exclude cases"

        Expect.equal
            offers
            [
                InstallationOffer.AuthorityAct AuthorityKind.RegisterActor
                InstallationOffer.AuthorityAct AuthorityKind.SetGrant
                InstallationOffer.AuthorityAct AuthorityKind.SetEnabled
            ]
            "An owner has administration advice without case authority"

        let! handler =
            grant admin (id 2) owner Role.CaseHandler GrantTarget.Installation true

        acceptedAuthority handler
        let! opened = admin.Execute(openRequest 3 "OFFER-1", CancellationToken.None)
        accepted opened |> ignore
        let target = service "scoped-reader"
        do! register admin 4 target

        let! scoped =
            grant admin (id 5) target Role.CaseRegistrar (GrantTarget.CaseReference "OFFER-1") true

        acceptedAuthority scoped
        let reader = core fixture target
        let! context = actorContext reader
        let offers = context.Offers

        Expect.isEmpty offers "A scoped registrar has no installation OPEN advice"

        Expect.equal
            context.Views
            [ View.Cases; View.Operations ]
            "Assigned case roles supply case views"

        let! revoked =
            grant admin (id 6) target Role.CaseRegistrar (GrantTarget.CaseReference "OFFER-1") false

        acceptedAuthority revoked
        let! context = actorContext reader
        let offers = context.Offers
        Expect.isEmpty offers "A fresh context observes revocation without cached offers"
        Expect.isEmpty context.Views "Revoked reads remove every view"
    }

[<Tests>]
let tests =
    testList
        "authority decisions and review"
        [
            testCaseAsync
                "installation offers respect scope principal and revocation"
                (run contextOffers)
            testCaseAsync "last enabled human installation owner is protected" (run lastOwner)
            testCaseAsync
                "authority replay retains target executor and position and conflicts on changed content"
                (run exactReplay)
            testCaseAsync
                "owner provisioning is explicit and exact replay is idempotent"
                (run provisionReplay)
            testCaseAsync
                "owner provisioning refuses service principals without records"
                (run serviceBootstrap)
            testCaseAsync
                "human-only grant targets are enforced under the lock"
                (run humanGrantTargets)
            testCaseAsync
                "authority uncertainty preserves content and reconciles by exact retry"
                (run uncertainty)
            testCaseAsync
                "authority failure and cancellation record nothing"
                (run failureAndCancellation)
            testCaseAsync
                "enabled actor context and installation access review have distinct permissions"
                (run contextAndReview)
            testCaseAsync
                "unified observation applies case and installation authority rights"
                (run observationRights)
        ]
