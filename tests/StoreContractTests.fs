module ClaimCore.TestSupport.StoreContractTests

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

let internal request n reference version command : CommandRequest =
    {
        OperationId = id n
        CaseReference = reference
        ExpectedVersion = version
        Command = command
    }

let internal registration: RegistrationInput =
    {
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "Lithuania"
        ClaimantName = "Synthetic Party"
        InsurerName = "Synthetic Insurer"
        ClaimedAmount = "1000.00"
        ClaimedCurrency = "EUR"
    }

let internal accepted =
    function
    | ExecuteOutcome.Accepted receipt -> receipt
    | _ -> failtest "Expected accepted operation."

let internal applied =
    function
    | AuthorityOutcome.Applied _ -> ()
    | _ -> failtest "Expected applied authority event."

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
                AuthorityDecisions.decide
                    fixture.InstallationId
                    fixture.Owner
                    (id 1)
                    fixture.Owner
                    AuthorityAction.ProvisionOwner,
                CancellationToken.None
            )

        match result with
        | Ok(AuthorityWriteResult.Appended _) -> ()
        | _ -> failtest "Initial owner must be accepted."

        return core fixture fixture.Owner
    }

let internal editor fixture =
    task {
        let! owner = bootstrap fixture

        let! grant =
            owner.Authority.SetGrant(
                id 2,
                fixture.Owner,
                Role.CaseEditor,
                GrantTarget.Installation,
                true,
                CancellationToken.None
            )

        applied grant
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

let private genesis fixture =
    task {
        let mutable calls = 0

        let decide (view: AuthorityLockedView) =
            calls <- calls + 1
            Expect.equal view.Head.Sequence 0L "Genesis head"

            Expect.equal view.Head.EntryHash (Array.zeroCreate 32) "Zero previous hash"

            Expect.isNone view.Actor "No actor before provisioning"

            AuthorityDecisions.decide
                fixture.InstallationId
                fixture.Owner
                (id 1)
                fixture.Owner
                AuthorityAction.ProvisionOwner
                view

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
        | Ok(AuthorityWriteResult.Appended entry) -> Expect.equal entry.Sequence 1L "First sequence"
        | _ -> failtest "Expected append"

        Expect.equal calls 1 "One decision"
        let! actor = fixture.Store.ReadActor(fixture.Owner, CancellationToken.None)

        match actor with
        | Ok { Actor = Some actor } ->
            Expect.equal
                actor.Grants
                [
                    {
                        Role = Role.Owner
                        Scope = GrantScope.Installation
                    }
                ]
                "Explicit initial grant"
        | _ -> failtest "Missing owner projection"

        let! entry = fixture.Store.ReadOperation(fixture.Owner, id 1, CancellationToken.None)

        match entry with
        | Ok(_, Some entry, _) -> Expect.equal entry.Sequence 1L "Readable acceptance"
        | _ -> failtest "Missing accepted entry"
    }

let private replayAndRefusal fixture =
    task {
        let! actor = editor fixture
        let original = request 10 "CONTRACT-001" 0L (Command.Open registration)
        let! result = actor.Execute(original, CancellationToken.None)
        let receipt = accepted result

        Expect.equal receipt.Position.Sequence 3L "One link after each authority event"

        let! replay = actor.Execute(original, CancellationToken.None)

        match replay with
        | ExecuteOutcome.AlreadyAccepted value -> Expect.equal value receipt "Original receipt"
        | _ -> failtest "Exact replay missing"

        let! stale =
            actor.Execute(request 11 "CONTRACT-001" 0L Command.Close, CancellationToken.None)

        Expect.equal
            stale
            (ExecuteOutcome.Rejected(Rejection.Domain(DomainError.VersionConflict 1L)))
            "No stale acceptance"

        let! view =
            fixture.Store.ReadCase(fixture.Owner, None, "CONTRACT-001", CancellationToken.None)

        match view with
        | Ok {
                 Case = Some {
                                 State = StoredCaseState.Live(snapshot, _)
                             }
             } -> Expect.equal (Some snapshot) receipt.Snapshot "Projection and receipt agree"
        | _ -> failtest "Missing case"

        let! history =
            fixture.Store.ReadHistory(fixture.Owner, "CONTRACT-001", 0L, 50, CancellationToken.None)

        match history with
        | Ok page -> Expect.equal page.Entries.Length 1 "Replay/refusal append nothing"
        | _ -> failtest "Missing history"
    }

let private lockedView fixture =
    task {
        let! actor = editor fixture
        let openRequest = request 10 "CONTRACT-001" 0L (Command.Open registration)
        let! opened = actor.Execute(openRequest, CancellationToken.None)
        let first = accepted opened
        let mutable calls = 0

        let decide (view: CaseLockedView) =
            calls <- calls + 1
            Expect.equal view.Head.Sequence first.Position.Sequence "Current head"

            Expect.equal
                (Convert.ToHexStringLower view.Head.EntryHash)
                first.Position.EntryHash
                "Previous accepted link"

            Expect.equal (view.Case |> Option.map _.Revision) (Some 1L) "Current case"

            Expect.equal
                (view.ExistingOperation |> Option.map _.OperationId)
                (Some openRequest.OperationId)
                "Existing operation under the same view"

            CaseDecision.Replay(Option.get view.ExistingOperation)

        let! result =
            fixture.Store.WriteCase(
                fixture.Owner,
                openRequest.OperationId,
                openRequest.CaseReference,
                decide,
                CancellationToken.None
            )

        match result with
        | Ok(CaseWriteResult.Replayed _) -> ()
        | _ -> failtest "Expected replay"

        Expect.equal calls 1 "Callback called once"
    }

let private caseUpdate fixture =
    task {
        let! actor = editor fixture

        let! opened =
            actor.Execute(
                request 10 "CONTRACT-001" 0L (Command.Open registration),
                CancellationToken.None
            )

        accepted opened |> ignore

        let! closed =
            actor.Execute(request 11 "CONTRACT-001" 1L Command.Close, CancellationToken.None)

        let receipt = accepted closed
        Expect.equal receipt.Revision 2L "One revision"

        Expect.equal
            (receipt.Snapshot |> Option.map (fun value -> value.Fields.Status))
            (Some CaseStatus.Closed)
            "Closure stored"

        let! history =
            fixture.Store.ReadHistory(fixture.Owner, "CONTRACT-001", 0L, 1, CancellationToken.None)

        match history with
        | Ok page ->
            Expect.equal page.Entries.Length 1 "Bounded page"
            Expect.equal page.NextAfterRevision (Some 1L) "Continuation"
        | _ -> failtest "Missing history"

        let! operation =
            fixture.Store.ReadOperation(fixture.Owner, id 11, CancellationToken.None)

        match operation with
        | Ok(_, Some entry, Some case) ->
            Expect.equal entry.Sequence receipt.Position.Sequence "Operation position"

            Expect.equal case.Revision 2L "Current projection"
        | _ -> failtest "Missing accepted update"
    }

let private erasureReadback fixture (actor: IActorClaimsCore) =
    task {
        let! history =
            fixture.Store.ReadHistory(fixture.Owner, "CONTRACT-001", 0L, 50, CancellationToken.None)

        match history with
        | Ok {
                 Case = Some { State = StoredCaseState.Erased }
                 Entries = entries
             } ->
            Expect.equal entries.Length 2 "Full accepted metadata remains"

            Expect.isTrue
                (entries
                 |> List.forall (fun entry -> entry.Payload.IsNone && entry.RequestSha256.IsNone))
                "All erasable entry data removed"
        | _ -> failtest "Missing erased projection"

        let! other =
            fixture.Store.ReadCase(fixture.Owner, None, "CONTRACT-002", CancellationToken.None)

        match other with
        | Ok {
                 Case = Some { State = StoredCaseState.Live _ }
             } -> ()
        | _ -> failtest "Other case was changed"

        let! replay =
            actor.Execute(
                request 10 "CONTRACT-001" 0L (Command.Open registration),
                CancellationToken.None
            )

        Expect.equal
            replay
            (ExecuteOutcome.Rejected Rejection.ResourceUnavailable)
            "Erased replay unavailable"

        let! reopen =
            actor.Execute(
                request 13 "CONTRACT-001" 0L (Command.Open registration),
                CancellationToken.None
            )

        Expect.equal
            reopen
            (ExecuteOutcome.Rejected Rejection.CaseErased)
            "Reference remains reserved"
    }

let private caseErasure fixture =
    task {
        let! actor = editor fixture

        let! grant =
            actor.Authority.SetGrant(
                id 3,
                fixture.Owner,
                Role.DataSteward,
                GrantTarget.Installation,
                true,
                CancellationToken.None
            )

        applied grant

        for n, reference in [ 10, "CONTRACT-001"; 11, "CONTRACT-002" ] do
            let! opened =
                actor.Execute(
                    request n reference 0L (Command.Open registration),
                    CancellationToken.None
                )

            accepted opened |> ignore

        let erase: StewardshipRequest =
            {
                OperationId = id 12
                CaseReference = "CONTRACT-001"
                ExpectedVersion = 1L
                Action = StewardshipAction.Erase
            }

        let! result = actor.Steward(erase, CancellationToken.None)

        Expect.isNone (accepted result).Snapshot "Erasure receipt contains no snapshot"

        do! erasureReadback fixture actor
    }

let private caseVisibility fixture =
    task {
        let! actor = editor fixture

        for n, reference in [ 10, "C"; 11, "A"; 12, "B" ] do
            let! result =
                actor.Execute(
                    request n reference 0L (Command.Open registration),
                    CancellationToken.None
                )

            accepted result |> ignore

        let! page =
            fixture.Store.ListCases(
                fixture.Owner,
                (fun _ -> Some CaseVisibility.Every),
                None,
                2,
                CancellationToken.None
            )

        match page with
        | Ok(Some page) ->
            Expect.equal (page.Items |> List.map _.Reference) [ "A"; "B" ] "Ordinal page"

            Expect.equal page.NextAfterReference (Some "B") "Last visible key"
        | _ -> failtest "Missing page"

        let! only =
            fixture.Store.ListCases(
                fixture.Owner,
                (fun _ -> Some(CaseVisibility.Only [ id 10 ])),
                None,
                1,
                CancellationToken.None
            )

        match only with
        | Ok(Some page) ->
            Expect.equal (page.Items |> List.map _.Reference) [ "C" ] "Visibility precedes limit"
        | _ -> failtest "Missing scoped page"

        let! next =
            fixture.Store.ListCases(
                fixture.Owner,
                (fun _ -> Some CaseVisibility.Every),
                Some "B",
                2,
                CancellationToken.None
            )

        match next with
        | Ok(Some page) -> Expect.equal (page.Items |> List.map _.Reference) [ "C" ] "Continuation"
        | _ -> failtest "Missing next page"
    }

let private registerReader (owner: IActorClaimsCore) target =
    task {
        let! registered = owner.Authority.RegisterActor(id 2, target, CancellationToken.None)
        applied registered

        let! added =
            owner.Authority.SetGrant(
                id 3,
                target,
                Role.CaseReader,
                GrantTarget.Installation,
                true,
                CancellationToken.None
            )

        applied added
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
        | Ok { Actor = Some actor } ->
            Expect.equal actor.ActorId (id 2) "Stable registration identity"

            Expect.equal
                actor.Grants
                [
                    {
                        Role = Role.CaseReader
                        Scope = GrantScope.Installation
                    }
                ]
                "Added grant"
        | _ -> failtest "Missing actor"

        let! removed =
            owner.Authority.SetGrant(
                id 4,
                target,
                Role.CaseReader,
                GrantTarget.Installation,
                false,
                CancellationToken.None
            )

        applied removed

        let! disabled =
            owner.Authority.SetEnabled(id 5, target, false, CancellationToken.None)

        applied disabled
        let! read = fixture.Store.ReadActor(target, CancellationToken.None)

        match read with
        | Ok { Actor = Some actor } ->
            Expect.isFalse actor.Enabled "Disabled"
            Expect.isEmpty actor.Grants "Removed"
        | _ -> failtest "Missing actor"
    }

let internal tests factory =
    testList
        "ledger store contract"
        [
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
                "authority changes and reads agree within snapshots"
                (withFixture factory authorityChanges)
        ]
