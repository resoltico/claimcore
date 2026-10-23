module ClaimCore.TestSupport.StoreContractTests

open ClaimCore.Contract

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal Fixture =
    {
        Store: ILedgerStore
        InstallationId: Guid
        Owner: PrincipalKey
        Calendar: IBusinessCalendar
        Dispose: unit -> unit
    }

let internal id n =
    Guid.Parse(sprintf "80000000-0000-4000-8000-%012d" n)


let internal request n reference revision command : CaseRequest =
    {
        OperationId = id n
        CaseReference = reference
        ExpectedRevision = revision
        Act = command
    }

let internal actRequest n reference revision act : CaseRequest =
    {
        OperationId = id n
        CaseReference = reference
        ExpectedRevision = revision
        Act = act
    }

let internal authorityRequest operationId target act : AuthorityRequest =
    {
        OperationId = operationId
        Target = target
        Act = act
    }

let internal registration: Registration =
    {
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "LV"
        ClaimantName = "Synthetic Party"
        InsurerName = "Synthetic Insurer"
        ClaimedAmount = "1000.00"
        ClaimedCurrency = "EUR"
    }

let internal accepted =
    function
    | ExecuteOutcome.Accepted receipt -> receipt
    | _ -> failtest "Expected accepted operation."

let internal acceptedAuthority outcome =
    match accepted outcome with
    | { Scope = ReceiptScope.Authority _ } -> ()
    | _ -> failtest "Expected accepted authority operation."

let internal caseReceipt (receipt: OperationReceipt) =
    match receipt.Scope with
    | ReceiptScope.Case value -> value
    | _ -> failtest "Expected case receipt."

let internal disclosedFields receipt =
    match (caseReceipt receipt).Snapshot with
    | SnapshotDisclosure.Disclosed(CaseView.Live(fields, _)) -> fields
    | _ -> failtest "Expected disclosed live fields."

let internal core fixture principal =
    Core.create fixture.InstallationId fixture.Store fixture.Calendar principal

let internal bootstrap fixture =
    task {
        let! result =
            fixture.Store.WriteAuthority(
                fixture.Owner,
                id 1,
                fixture.Owner,
                None,
                AuthorityDecisions.provision fixture.InstallationId (id 1) fixture.Owner,
                CancellationToken.None
            )

        match result with
        | Ok(LedgerWriteResult.Appended _) -> ()
        | _ -> failtest "Initial owner must be accepted."

        return core fixture fixture.Owner
    }

let internal grant (actor: IActorClaimsCore) operationId target role scope active =
    actor.ExecuteAuthority(
        authorityRequest operationId target (AuthorityAct.SetGrant(role, scope, active)),
        CancellationToken.None
    )

let internal caseWorker fixture =
    task {
        let! owner = bootstrap fixture

        for operationId, role in [ id 2, Role.CaseHandler; id 900000, Role.PaymentRecorder ] do
            let! outcome =
                grant owner operationId fixture.Owner role GrantTarget.Installation true

            acceptedAuthority outcome

        return owner
    }

let private withFixture factory run =
    async {
        let! fixture = factory () |> Async.AwaitTask

        try
            do! run fixture |> Async.AwaitTask
        finally
            fixture.Dispose()
    }

let private storedPage result =
    result
    |> Result.defaultWith (fun _ -> failtest "Store read failed")
    |> Result.defaultWith (fun _ -> failtest "Store selection refused")

let private history fixture reference after limit =
    fixture.Store.ReadHistory(
        fixture.Owner,
        reference,
        (fun _ -> Ok after),
        limit,
        CancellationToken.None
    )

let private listCases fixture visibility after limit =
    fixture.Store.ListCases(
        fixture.Owner,
        ActorAuthorization.readRoles fixture.Owner,
        (fun _ ->
            Ok
                {
                    Visibility = visibility
                    AfterReference = after
                }),
        limit,
        CancellationToken.None
    )

let private genesis fixture =
    task {
        let mutable calls = 0

        let decide (view: AuthorityLockedView) =
            calls <- calls + 1
            Expect.equal view.Head.Sequence 0L "Genesis head"
            Expect.isTrue (view.Head.EntryHash = Array.zeroCreate 32) "Zero previous hash"
            Expect.isNone view.Actor.Actor "No actor before provisioning"
            AuthorityDecisions.provision fixture.InstallationId (id 1) fixture.Owner view

        let! result =
            fixture.Store.WriteAuthority(
                fixture.Owner,
                id 1,
                fixture.Owner,
                None,
                decide,
                CancellationToken.None
            )

        match result with
        | Ok(LedgerWriteResult.Appended receipt) ->
            Expect.equal receipt.Position.Sequence 1L "First sequence"
        | _ -> failtest "Expected append"

        Expect.equal calls 1 "One decision"
        let! actor = fixture.Store.ReadActor(fixture.Owner, CancellationToken.None)

        match actor with
        | Ok {
                 Actor = ({ Actor = Some _ } as authority)
             } ->
            Expect.equal authority.Installation [ Role.Owner ] "Only the owner role is provisioned"
        | _ -> failtest "Missing owner projection"

        let! entry = fixture.Store.ReadOperation(fixture.Owner, id 1, CancellationToken.None)

        match entry with
        | Ok { Entry = Some entry } -> Expect.equal entry.Sequence 1L "Readable acceptance"
        | _ -> failtest "Missing accepted entry"
    }

let private replayAndRefusal fixture =
    task {
        let! actor = caseWorker fixture
        let original = request 10 "CONTRACT-001" 0L (CaseAct.Open registration)
        let! result = actor.Execute(original, CancellationToken.None)
        let receipt = accepted result

        Expect.equal
            receipt.Position.Sequence
            4L
            "One entry per authority operation and case operation"

        let! replay = actor.Execute(original, CancellationToken.None)
        Expect.equal replay (ExecuteOutcome.AlreadyAccepted receipt) "Original receipt"

        let! stale =
            actor.Execute(request 11 "CONTRACT-001" 0L CaseAct.Close, CancellationToken.None)

        Expect.equal
            stale
            (ExecuteOutcome.Rejected(Rejection.Case(CaseRefusal.RevisionConflict 1L)))
            "No stale acceptance"

        let! view =
            fixture.Store.ReadCase(fixture.Owner, None, "CONTRACT-001", CancellationToken.None)

        match view with
        | Ok { Case = Some record } ->
            Expect.equal
                (caseReceipt receipt).Snapshot
                (SnapshotDisclosure.Disclosed record.Snapshot.View)
                "Projection and receipt agree"
        | _ -> failtest "Missing case"

        let! rows = history fixture "CONTRACT-001" 0L 50
        Expect.equal (storedPage rows).Entries.Length 1 "Replay/refusal append nothing"
    }

let private lockedView fixture =
    task {
        let! actor = caseWorker fixture
        let original = request 10 "CONTRACT-001" 0L (CaseAct.Open registration)
        let! opened = actor.Execute(original, CancellationToken.None)
        let first = accepted opened
        let mutable calls = 0

        let decide (view: CaseLockedView) =
            calls <- calls + 1
            Expect.equal view.Head.Sequence first.Position.Sequence "Current head"

            Expect.equal
                (Convert.ToHexStringLower view.Head.EntryHash)
                first.Position.EntryHash
                "Previous accepted link"

            Expect.equal
                (view.Case |> Option.map (fun record -> record.Snapshot.Revision))
                (Some 1L)
                "Current case"

            Expect.equal
                (view.ExistingOperation |> Option.map _.OperationId)
                (Some original.OperationId)
                "Existing identity in the same view"

            CaseDecisions.execute
                fixture.InstallationId
                fixture.Calendar
                fixture.Owner
                original
                view

        let! result =
            fixture.Store.WriteCase(
                fixture.Owner,
                original.OperationId,
                original.CaseReference,
                decide,
                CancellationToken.None
            )

        match result with
        | Ok(LedgerWriteResult.Replayed receipt) -> Expect.equal receipt first "Locked replay"
        | _ -> failtest "Expected replay"

        Expect.equal calls 1 "Callback called once"
    }

let private caseUpdate fixture =
    task {
        let! actor = caseWorker fixture

        let! opened =
            actor.Execute(
                request 10 "CONTRACT-001" 0L (CaseAct.Open registration),
                CancellationToken.None
            )

        accepted opened |> ignore

        let! closed =
            actor.Execute(request 11 "CONTRACT-001" 1L CaseAct.Close, CancellationToken.None)

        let receipt = accepted closed
        Expect.equal (caseReceipt receipt).Revision 2L "One revision"
        Expect.equal (disclosedFields receipt).Status CaseStatus.Closed "Closure stored"
        let! rows = history fixture "CONTRACT-001" 0L 1
        let page = storedPage rows
        Expect.equal page.Entries.Length 1 "Bounded page"
        Expect.equal page.NextAfterRevision (Some 1L) "Continuation"

        let! operation =
            fixture.Store.ReadOperation(fixture.Owner, id 11, CancellationToken.None)

        match operation with
        | Ok {
                 Entry = Some entry
                 Case = Some record
             } ->
            Expect.equal entry.Sequence receipt.Position.Sequence "Operation position"
            Expect.equal record.Snapshot.Revision 2L "Current projection"
        | _ -> failtest "Missing accepted update"
    }

let private erasureReadback fixture (actor: IActorClaimsCore) =
    task {
        let! rows = history fixture "CONTRACT-001" 0L 50
        let page = storedPage rows

        match page.Case with
        | Some {
                   Snapshot = { View = CaseView.Erased }
               } -> ()
        | _ -> failtest "Missing erased projection"

        Expect.equal page.Entries.Length 2 "Full accepted metadata remains"

        Expect.isTrue
            (page.Entries
             |> List.forall (fun entry ->
                 entry.Payload.IsNone
                 && entry.PayloadNonce.IsNone
                 && entry.PayloadCommitment.Length = 32))
            "Only erasable columns are cleared"

        let! other =
            fixture.Store.ReadCase(fixture.Owner, None, "CONTRACT-002", CancellationToken.None)

        match other with
        | Ok {
                 Case = Some {
                                 Snapshot = { View = CaseView.Live _ }
                             }
             } -> ()
        | _ -> failtest "Other case changed"

        let! replay =
            actor.Execute(
                request 10 "CONTRACT-001" 0L (CaseAct.Open registration),
                CancellationToken.None
            )

        Expect.equal
            replay
            (ExecuteOutcome.Rejected Rejection.ResourceUnavailable)
            "Erased requests cannot replay"

        let! reopen =
            actor.Execute(
                request 13 "contract-001" 0L (CaseAct.Open registration),
                CancellationToken.None
            )

        Expect.equal
            reopen
            (ExecuteOutcome.Rejected(Rejection.Case CaseRefusal.CaseErased))
            "Folded reference remains reserved"
    }

let private caseErasure fixture =
    task {
        let! actor = caseWorker fixture

        let! granted =
            grant actor (id 3) fixture.Owner Role.DataSteward GrantTarget.Installation true

        acceptedAuthority granted

        for n, reference in [ 10, "CONTRACT-001"; 11, "CONTRACT-002" ] do
            let! opened =
                actor.Execute(
                    request n reference 0L (CaseAct.Open registration),
                    CancellationToken.None
                )

            accepted opened |> ignore

        let erase = actRequest 12 "CONTRACT-001" 1L (CaseAct.Erase)

        let! result = actor.Execute(erase, CancellationToken.None)

        Expect.equal
            (accepted result |> caseReceipt).Snapshot
            SnapshotDisclosure.Erased
            "Erasure is distinct from withholding"

        do! erasureReadback fixture actor
    }

let private existingCaseId fixture reference =
    task {
        let! result =
            fixture.Store.ReadCase(fixture.Owner, None, reference, CancellationToken.None)

        return
            match result with
            | Ok { Case = Some record } -> record.CaseId
            | _ -> failtest "Expected existing case identity"
    }

let private assignedReader (actor: IActorClaimsCore) =
    task {
        let principal =
            PrincipalKey.human "https://issuer.example/" "assigned-reader"
            |> Result.defaultWith failtest

        let! registered =
            actor.ExecuteAuthority(
                authorityRequest (id 15) principal AuthorityAct.RegisterActor,
                CancellationToken.None
            )

        acceptedAuthority registered

        let! granted =
            grant actor (id 16) principal Role.CaseReader (GrantTarget.CaseReference "C") true

        acceptedAuthority granted

        return principal
    }

let private caseVisibility fixture =
    task {
        let! actor = caseWorker fixture

        for n, reference in [ 10, "C"; 11, "A"; 12, "B" ] do
            let! result =
                actor.Execute(
                    request n reference 0L (CaseAct.Open registration),
                    CancellationToken.None
                )

            accepted result |> ignore

        let! first = listCases fixture CaseVisibility.Every None 2
        let first = storedPage first

        Expect.equal
            (first.Items |> List.map (fun record -> record.Snapshot.Reference))
            [ "A"; "B" ]
            "Reference-key order"

        Expect.equal first.NextAfterReference (Some "B") "Last visible key"

        let! principal = assignedReader actor

        let! only =
            fixture.Store.ListCases(
                principal,
                ActorAuthorization.readRoles principal,
                (fun _ ->
                    Ok
                        {
                            Visibility = CaseVisibility.Assigned
                            AfterReference = None
                        }),
                1,
                CancellationToken.None
            )

        Expect.equal
            ((storedPage only).Items |> List.map (fun record -> record.Snapshot.Reference))
            [ "C" ]
            "Visibility precedes limit"

        let! next = listCases fixture CaseVisibility.Every (Some "B") 2

        Expect.equal
            ((storedPage next).Items |> List.map (fun record -> record.Snapshot.Reference))
            [ "C" ]
            "Continuation"
    }

let private registerReader (owner: IActorClaimsCore) target =
    task {
        let! registered =
            owner.ExecuteAuthority(
                authorityRequest (id 2) target AuthorityAct.RegisterActor,
                CancellationToken.None
            )

        acceptedAuthority registered
        let! added = grant owner (id 3) target Role.CaseReader GrantTarget.Installation true
        acceptedAuthority added
    }

let private authorityChanges fixture =
    task {
        let! owner = bootstrap fixture

        let target =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith failtest

        do! registerReader owner target
        let! read = fixture.Store.ReadActor(target, CancellationToken.None)

        match read with
        | Ok {
                 Actor = ({ Actor = Some actor } as authority)
             } ->
            Expect.equal actor.ActorId (id 2) "Registration identity"

            Expect.equal authority.Installation [ Role.CaseReader ] "Added grant"
        | _ -> failtest "Missing actor"

        let! removed =
            grant owner (id 4) target Role.CaseReader GrantTarget.Installation false

        acceptedAuthority removed

        let! disabled =
            owner.ExecuteAuthority(
                authorityRequest (id 5) target (AuthorityAct.SetEnabled false),
                CancellationToken.None
            )

        acceptedAuthority disabled
        let! read = fixture.Store.ReadActor(target, CancellationToken.None)

        match read with
        | Ok {
                 Actor = ({ Actor = Some actor } as authority)
             } ->
            Expect.isFalse actor.Enabled "Disabled"
            Expect.isEmpty authority.Installation "Removed"
        | _ -> failtest "Missing actor"
    }

let private foldedOrdering fixture =
    task {
        let! actor = caseWorker fixture

        for n, reference in [ 20, "Ref-b"; 21, "ref-A"; 22, "REF-C" ] do
            let! result =
                actor.Execute(
                    request n reference 0L (CaseAct.Open registration),
                    CancellationToken.None
                )

            accepted result |> ignore

        let! first = listCases fixture CaseVisibility.Every None 1
        let first = storedPage first

        Expect.equal
            (first.Items |> List.map (fun record -> record.Snapshot.Reference))
            [ "ref-A" ]
            "Authored spelling with ASCII-key order"

        Expect.equal first.NextAfterReference (Some "REF-A") "Continuation is folded"
        let! second = listCases fixture CaseVisibility.Every first.NextAfterReference 1

        Expect.equal
            ((storedPage second).Items |> List.map (fun record -> record.Snapshot.Reference))
            [ "Ref-b" ]
            "Continuation uses the same order"
    }

let private scopedHandler fixture (admin: IActorClaimsCore) reference =
    task {
        let target =
            PrincipalKey.service "https://issuer.example/" "scoped-handler"
            |> Result.defaultWith failtest

        let! registered =
            admin.ExecuteAuthority(
                authorityRequest (id 30) target AuthorityAct.RegisterActor,
                CancellationToken.None
            )

        acceptedAuthority registered

        let! granted =
            grant admin (id 31) target Role.CaseHandler (GrantTarget.CaseReference reference) true

        acceptedAuthority granted
        return target, core fixture target
    }

let private checkScopedAdvice (actor: IActorClaimsCore) reference =
    task {
        let! result = actor.Get(reference, CancellationToken.None)

        match result with
        | QueryOutcome.Succeeded value ->
            Expect.equal
                value.Advice.History
                [ HistoryDetail.Summary; HistoryDetail.Full ]
                "Case-scoped full history is advised"

            let expected, correction =
                value.Advice.Acts
                |> List.pick (function
                    | CaseActOffer.CorrectCase(expected, choices) -> Some(expected, choices)
                    | _ -> None)

            Expect.equal expected 2L "Advice binds the actual revision"

            Expect.equal
                correction.Payment
                [ CorrectionGroupAction.Keep ]
                "Absent payment and absent capability require explicit KEEP"
        | _ -> failtest "Expected readable scoped case"
    }

let private advisedCase fixture reference =
    task {
        let! admin = caseWorker fixture

        let! opened =
            admin.Execute(
                request 20 reference 0L (CaseAct.Open registration),
                CancellationToken.None
            )

        accepted opened |> ignore

        let decision =
            {
                PaymentDecisionDate = "2026-08-15"
                PayableAmount = "750.0000"
                PayableCurrency = "EUR"
            }

        let! decided =
            admin.Execute(request 21 reference 1L (CaseAct.Decide decision), CancellationToken.None)

        accepted decided |> ignore
        return admin
    }

let private refusedAdvice fixture (actor: IActorClaimsCore) pending =
    task {
        let! refused = actor.Execute(pending, CancellationToken.None)

        Expect.equal
            refused
            (ExecuteOutcome.Rejected Rejection.ActNotPermitted)
            "Revocation is decided again under lock"

        let! current = actor.Get(pending.CaseReference, CancellationToken.None)

        match current with
        | QueryOutcome.Succeeded value ->
            Expect.equal value.Snapshot.Revision 2L "Refusal preserves the projection"

            Expect.equal
                value.Advice.History
                [ HistoryDetail.Summary ]
                "Fresh advice reflects revoked full history"

            Expect.isFalse
                (value.Advice.Acts
                 |> List.exists (function
                     | CaseActOffer.CorrectCase _ -> true
                     | _ -> false))
                "No stale act offer"
        | _ -> failtest "Reader remains authorized"

        let! entries = history fixture pending.CaseReference 0L 50

        Expect.equal
            (storedPage entries).Entries.Length
            2
            "Revoked execution appends no case acceptance"
    }

let private revokedAdvice fixture =
    task {
        let reference = "SCOPED-ADVICE"
        let! admin = advisedCase fixture reference
        let! target, actor = scopedHandler fixture admin reference
        do! checkScopedAdvice actor reference

        let correction: Correction =
            {
                Registration =
                    RegistrationGroup.Replace
                        { registration with
                            ClaimedAmount = "1001.0000"
                        }
                Decision = DecisionGroup.Keep
                Payment = PaymentGroup.Keep
            }

        let pending = request 35 reference 2L (CaseAct.CorrectCase correction)
        let! preview = actor.Preview(pending, CancellationToken.None)

        match preview with
        | PreviewOutcome.Reviewed _ -> ()
        | _ -> failtest "Expected authoritative correction review"

        let! removed =
            grant admin (id 32) target Role.CaseHandler (GrantTarget.CaseReference reference) false

        acceptedAuthority removed

        let! reader =
            grant admin (id 33) target Role.CaseReader (GrantTarget.CaseReference reference) true

        acceptedAuthority reader
        do! refusedAdvice fixture actor pending
    }

let private scopedWorker fixture =
    task {
        let! admin = caseWorker fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "many-scopes"
            |> Result.defaultWith failtest

        let! registered =
            admin.ExecuteAuthority(
                authorityRequest (id 20) principal AuthorityAct.RegisterActor,
                CancellationToken.None
            )

        acceptedAuthority registered

        let! installed =
            grant admin (id 21) principal Role.CaseRegistrar GrantTarget.Installation true

        acceptedAuthority installed

        for number in 1..12 do
            let reference = $"SCOPE-{number}"

            let! opened =
                admin.Execute(
                    request (100 + number) reference 0L (CaseAct.Open registration),
                    CancellationToken.None
                )

            accepted opened |> ignore

            let! granted =
                grant
                    admin
                    (id (200 + number))
                    principal
                    Role.CaseReader
                    (GrantTarget.CaseReference reference)
                    true

            acceptedAuthority granted

        return principal
    }

let private scopedAuthority fixture =
    task {
        let! principal = scopedWorker fixture

        let! read =
            fixture.Store.ReadCase(principal, None, "SCOPE-1", CancellationToken.None)

        let view = read |> Result.defaultWith (fun _ -> failtest "Scoped case read")

        Expect.equal
            view.Actor.Installation
            [ Role.CaseRegistrar ]
            "Installation grants remain available"

        Expect.equal view.Actor.Cases.Count 1 "Unrelated case grants are not read"

        let! identity =
            fixture.Store.ReadCase(principal, Some(id 102), "SCOPE-1", CancellationToken.None)

        let identity = identity |> Result.defaultWith (fun _ -> failtest "Identity scopes")

        Expect.equal
            identity.Actor.Cases.Count
            2
            "Existing operation adds only its own case authority"

        let! observed =
            fixture.Store.ReadOperation(principal, id 102, CancellationToken.None)

        let observed = observed |> Result.defaultWith (fun _ -> failtest "Observe scopes")
        Expect.equal observed.Actor.Cases.Count 1 "Observation reads the accepted entry scope only"
        let! context = fixture.Store.ReadActor(principal, CancellationToken.None)
        let context = context |> Result.defaultWith (fun _ -> failtest "Context read")
        Expect.isEmpty context.Actor.Cases "Context never enumerates assigned grants"
        Expect.isTrue context.Actor.HasAssignedCases "EXISTS reports assigned visibility"

        let pending = request 102 "SCOPE-1" 1L (CaseAct.AmendRegistration registration)

        let! conflict =
            (Core.create fixture.InstallationId fixture.Store fixture.Calendar principal)
                .Execute(pending, CancellationToken.None)

        Expect.equal
            conflict
            (ExecuteOutcome.Rejected Rejection.IdempotencyConflict)
            "Both scopes preserve content exclusion"
    }

let internal tests factory =
    testList
        "ledger store contract"
        [
            testCaseAsync
                "authority loading stays bounded to the requested and accepted scopes"
                (withFixture factory scopedAuthority)
            testCaseAsync
                "scoped advice and preview never authorize execution after revocation"
                (withFixture factory revokedAdvice)
            testCaseAsync
                "genesis append loads one view and co-commits authority"
                (withFixture factory genesis)
            testCaseAsync
                "append replay and refusal preserve head and complete case projection"
                (withFixture factory replayAndRefusal)
            testCaseAsync
                "successive writes load the current head case and existing identity once"
                (withFixture factory lockedView)
            testCaseAsync
                "update advances revision and reads back complete fields"
                (withFixture factory caseUpdate)
            testCaseAsync
                "erasure clears every case payload and preserves other cases"
                (withFixture factory caseErasure)
            testCaseAsync
                "visibility ordering and keyset limits are applied before paging"
                (withFixture factory caseVisibility)
            testCaseAsync
                "ASCII reference folding and keyset continuation agree"
                (withFixture factory foldedOrdering)
            testCaseAsync
                "authority changes and reads agree within snapshots"
                (withFixture factory authorityChanges)
        ]
