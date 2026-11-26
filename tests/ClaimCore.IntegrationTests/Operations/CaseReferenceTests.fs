module ClaimCore.IntegrationTests.CaseReferenceTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Verification
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None
let private referenceField = Submissions.field BusinessField.CaseReference

let private value = Option.map CaseReference.format

let private first = "CC-0000-0000-0000-0000-98"
let private second = "CC-0000-0000-0000-0001-95"

let private seed number =
    let bytes = Array.zeroCreate<byte> 10
    bytes[9] <- number
    bytes

let private succeeds =
    function
    | Outcome.Succeeded result -> result
    | other -> failtestf "Expected success, received %A" other

let private run policy action =
    async {
        let! database = PostgresFixture.createWithReferencePolicy policy |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private withEntropy (database: PostgresFixture.Database) draw =
    let service = core database.Fixture database.Fixture.Owner

    { service with
        Store =
            LedgerStore(
                database.RuntimeSource,
                database.Installation,
                database.Cipher,
                Transactions.timeouts,
                caseReferenceEntropy = draw
            )
            :> IStore
    }

let private opening fixture number values =
    Submissions.create "case.open" (id fixture number) (Binding.CaseNew Revision.zero) values

let private custom fixture number text =
    opening fixture number (Map.ofList [ referenceField, SubmissionValue.Text text ])

let private readBy service selector =
    task {
        let! result = Service.get service { Target = selector } token
        return succeeds result
    }

let private acceptedLookup service operation =
    task {
        let! reply = Service.operationGet service { OperationId = operation } token

        match succeeds reply.Outcome with
        | OperationLookupResult.Accepted(receipt, _) -> return caseReceipt receipt
        | other -> return failtestf "Expected an accepted operation lookup, received %A" other
    }

let private assigned service submission =
    task {
        let! result = Service.executeSubmission service submission token
        return accepted result |> caseReceipt
    }

let private head (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        return! OperationStorage.position connection token
    }

let private verify (database: PostgresFixture.Database) =
    task {
        let! result = Verification.verify database.OwnerSource database.MasterKey None token

        match result with
        | VerificationResult.Verified _ -> ()
        | other ->
            failtestf "Expected complete retained and projection verification, received %A" other
    }

let private systemPolicy (database: PostgresFixture.Database) =
    task {
        let mutable draws = 0

        let service =
            withEntropy database (fun () ->
                draws <- draws + 1
                seed 0uy)

        let fixture = database.Fixture
        let supplied = custom fixture 10 "SYNTHETIC"
        let auto = opening fixture 11 Map.empty

        let cleared =
            opening fixture 12 (Map.ofList [ referenceField, SubmissionValue.Clear ])

        let! basis = head database
        let! context = Service.context service () token

        let offers =
            match (succeeds context).Requester with
            | RequesterState.Active actor -> actor.Offers
            | other -> failtestf "Expected active installer, received %A" other

        let! page =
            Service.list
                service
                {
                    Filter = CaseListFilter.activeOpen
                    Page = { AfterCursor = None; Limit = 5 }
                }
                token

        Expect.equal (succeeds page).Offers offers "Requester and Cases publish the same offer"

        match
            offers
            |> List.tryPick (function
                | InstallationOffer.Open editable -> Some editable
                | _ -> None)
        with
        | Some editable ->
            Expect.isFalse
                (List.contains referenceField editable)
                "The installed policy disables a custom input"
        | None -> failtest "The installer can open a case"

        let! preview = Service.previewSubmission service auto token

        match succeeds preview.Outcome with
        | Previewed.Reviewed review ->
            Expect.equal
                review.Target
                (ReviewTarget.Opening None)
                "Pending review has no routable identity"

            Expect.isSome review.Opening "Opening facts exist with all business fields absent"
            Expect.isEmpty review.Changes "There are no accepted effects in preview"
        | other -> failtestf "Expected a pending opening review, received %A" other

        for submission in [ supplied; cleared ] do
            let! refusal = Service.executeSubmission service submission token

            match refusal.Outcome with
            | ExecuteResult.Rejected(Refusal.InvalidInput _) -> ()
            | other -> failtestf "Supplied value is refused, received %A" other

        let machine = PrincipalKey.service "https://issuer.example/" "reference-import"
        let machine = machine |> Result.defaultWith (string >> failtest)

        let! registration =
            personSubmission
                service.Store
                (id fixture 13)
                machine
                None
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! admitted = Service.executeSubmission service registration token
        acceptedPeople admitted

        let! permission =
            grant
                service
                (id fixture 14)
                machine
                {
                    Role = Role.CaseRegistrar
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople permission
        let machineService = { service with Principal = machine }
        let! refused = Service.executeSubmission machineService supplied token

        match refused.Outcome with
        | ExecuteResult.Rejected(Refusal.InvalidInput _) -> ()
        | other -> failtestf "An authorized service account obeys the policy, received %A" other

        let! afterRefusals = head database

        Expect.equal
            (Sequence.value afterRefusals.Sequence)
            (Sequence.value basis.Sequence + 2L)
            "Only account setup appended"

        Expect.equal draws 0 "Review, syntax and policy refusals draw zero candidates"

        let! receipt = assigned machineService auto

        Expect.equal
            (CaseReference.format receipt.Reference)
            first
            "The first generated reference includes leading zeroes"

        Expect.equal
            (CaseId.value receipt.CaseId)
            auto.OperationId
            "Opening operation assigns the stable Case ID"

        let! replay = Service.executeSubmission machineService auto token

        Expect.equal
            (replayReceipt replay |> caseReceipt).Reference
            receipt.Reference
            "Replay returns the accepted issuance"

        Expect.equal draws 1 "Accepted replay never allocates a new candidate"

        let changed = Submissions.withOperationId auto.OperationId supplied
        let! conflict = Service.executeSubmission machineService changed token

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Supplying a valid override changes the earlier omitted intent"

        Expect.equal draws 1 "An operation identity conflict does not allocate"

        let! selected = readBy service (CaseSelector.Reference(first.ToLowerInvariant()))

        Expect.equal
            selected.Snapshot.CaseId
            receipt.CaseId
            "Case-insensitive exact lookup resolves generated spelling"

        let! malformed =
            Service.get
                service
                {
                    Target = CaseSelector.Reference "CC-0000-0000-0000-0000-01"
                }
                token

        match malformed with
        | Outcome.Rejected(Refusal.InvalidInput _) -> ()
        | other ->
            failtestf "An alternative congruent suffix is rejected before lookup, received %A" other

        let! search =
            Service.list
                service
                {
                    Filter = CaseListFilter.create [] [] [] (Some "cc-0000") CaseListOrder.Reference
                    Page = { AfterCursor = None; Limit = 5 }
                }
                token

        Expect.equal
            ((succeeds search).Items |> List.map _.CaseId)
            [ receipt.CaseId ]
            "Broad search keeps fragment semantics"

        do! verify database
    }

let private transactionalIssuance (database: PostgresFixture.Database) =
    task {
        let mutable draws = 0
        let mutable entropy = fun () -> seed 0uy

        let service =
            withEntropy database (fun () ->
                draws <- draws + 1
                entropy ())

        let fixture = database.Fixture
        let facts = Submissions.fields (RegistrationInput.values DomainInputs.registration)
        let original = opening fixture 20 facts

        let! results =
            Task.WhenAll(
                Service.executeSubmission service original token,
                Service.executeSubmission service original token
            )

        Expect.equal
            (results
             |> Array.filter (fun reply ->
                 match reply.Outcome with
                 | ExecuteResult.Accepted _ -> true
                 | _ -> false)
             |> Array.length)
            1
            "Concurrent submissions of one operation accept once"

        for result in results do
            match result.Outcome with
            | ExecuteResult.Accepted receipt
            | ExecuteResult.AlreadyAccepted receipt ->
                Expect.equal
                    (CaseReference.format (caseReceipt receipt).Reference)
                    first
                    "Both results report the original allocation"
            | other -> failtestf "Expected acceptance or replay, received %A" other

        Expect.equal draws 1 "The accepted-operation check wins before allocation"

        let collision = opening fixture 21 facts
        let! checkpoint = head database
        let! exhausted = Service.executeSubmission service collision token

        Expect.equal
            exhausted.Outcome
            (ExecuteResult.Failed CoreFault.InternalError)
            "Eight occupied candidates exhaust the attempt"

        Expect.equal draws 9 "Eight is the total draw limit"
        let! afterExhaustion = head database
        Expect.equal afterExhaustion checkpoint "Exhaustion causes no write"

        let! current = readBy service (CaseSelector.Reference first)
        let changeReference = TaskId.ofLiteral "case.changeReference"

        Expect.isFalse
            (List.contains changeReference current.Advice.Acts)
            "Generated cases omit the correction offer"

        let correction =
            Submissions.create
                "case.changeReference"
                (id fixture 22)
                (Binding.Case(CaseSelector.Reference first, Revision.first))
                (Map.ofList
                    [
                        Submissions.input InputTarget.NewCaseReference,
                        SubmissionValue.Text "REPLACEMENT"
                    ])

        let! rejected = Service.executeSubmission service correction token

        match rejected.Outcome with
        | ExecuteResult.Rejected(Refusal.Case(CaseRefusal.NotOffered _)) -> ()
        | other -> failtestf "Generated correction is inapplicable, received %A" other

        let erase = request fixture 23 first 1L "case.erase" Map.empty
        let! erased = Service.executeSubmission service erase token
        let erased = accepted erased |> caseReceipt

        Expect.equal
            erased.Snapshot
            SnapshotDisclosure.Erased
            "The first generated reservation survives erasure"

        entropy <- (fun () -> if draws = 10 then seed 0uy else seed 1uy)
        let! another = assigned service collision

        Expect.equal
            (CaseReference.format another.Reference)
            second
            "An erased reservation causes a fresh draw"

        Expect.equal draws 11 "Both candidate checks ran"

        let duplicate = opening fixture 24 Map.empty
        let! checkpoint = head database
        entropy <- (fun () -> raise (IO.IOException "Synthetic entropy failure"))
        let! unavailable = Service.executeSubmission service duplicate token

        Expect.isTrue
            (match unavailable.Outcome with
             | ExecuteResult.Failed _ -> true
             | _ -> false)
            "Entropy failure is operational"

        let! afterEntropy = head database
        Expect.equal afterEntropy checkpoint "Entropy failure changes no state"

        use! owner = database.OwnerSource.OpenConnectionAsync()

        let ddl text =
            DatabaseSession.execute owner text [||] token

        let! _ =
            ddl
                "CREATE FUNCTION claimcore.reject_test_case_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic projection failure'; END $$; CREATE TRIGGER reject_test_case_insert BEFORE INSERT ON claimcore.cases FOR EACH ROW EXECUTE FUNCTION claimcore.reject_test_case_insert()"

        entropy <- (fun () -> seed 2uy)
        let previousDraws = draws
        let! failed = Service.executeSubmission service duplicate token

        Expect.isTrue
            (match failed.Outcome with
             | ExecuteResult.Failed _ -> true
             | _ -> false)
            "Known server rejection is a definite failure"

        Expect.equal draws (previousDraws + 1) "A projection failure does not redraw"

        let! afterProjection = head database
        Expect.equal afterProjection checkpoint "Case, receipt and reservation roll back together"

        let! _ =
            ddl
                "DROP TRIGGER reject_test_case_insert ON claimcore.cases; DROP FUNCTION claimcore.reject_test_case_insert()"

        let! retry = assigned service duplicate

        Expect.equal
            (CaseReference.format retry.Reference)
            "CC-0000-0000-0000-0002-92"
            "The failed reservation is reusable"

        let previousDraws = draws

        let! competing =
            Task.WhenAll(
                Service.executeSubmission service (custom fixture 25 "Shared-Key") token,
                Service.executeSubmission service (custom fixture 26 "SHARED-KEY") token
            )

        let acceptedCount =
            competing
            |> Array.filter (fun reply ->
                match reply.Outcome with
                | ExecuteResult.Accepted _ -> true
                | _ -> false)
            |> Array.length

        Expect.equal acceptedCount 1 "Only one concurrent custom submission owns the folded key"

        Expect.isTrue
            (competing
             |> Array.exists (fun reply ->
                 reply.Outcome =
                     ExecuteResult.Rejected(Refusal.Case CaseRefusal.ReferenceUnavailable)))
            "The losing custom submission receives the generic refusal"

        Expect.equal draws previousDraws "Custom submissions never draw entropy"

        do! verify database
    }

let private receiptHistory (database: PostgresFixture.Database) =
    task {
        let service =
            withEntropy database (fun () -> failwith "Custom issuance never draws")

        let fixture = database.Fixture

        let requester =
            PrincipalKey.human "https://issuer.example/" "custom-reference-registrar"
            |> Result.defaultWith (string >> failtest)

        let! registration =
            personSubmission
                service.Store
                (id fixture 30)
                requester
                None
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! registered = Service.executeSubmission service registration token
        acceptedPeople registered

        let grantRole number scope active =
            grant
                service
                (id fixture number)
                requester
                {
                    Role = Role.CaseRegistrar
                    Scope = scope
                }
                active

        let! access = grantRole 31 GrantTarget.Installation true
        acceptedPeople access
        let registrar = { service with Principal = requester }

        let original =
            opening
                fixture
                32
                (Submissions.fields (RegistrationInput.values DomainInputs.registration)
                 |> Map.add referenceField (SubmissionValue.Text "On-Open"))

        let! receipt = assigned registrar original

        Expect.equal
            (CaseReference.format receipt.Reference)
            "On-Open"
            "Acceptance preserves the exact supplied spelling"

        let changed =
            request
                fixture
                33
                "On-Open"
                1L
                "case.changeReference"
                (Map.ofList
                    [
                        Submissions.input InputTarget.NewCaseReference,
                        SubmissionValue.Text "CURRENT"
                    ])

        let! corrected = assigned service changed

        Expect.equal
            (CaseReference.format corrected.Reference)
            "CURRENT"
            "The correction receipt keeps the current label"

        let! current = readBy service (CaseSelector.Id receipt.CaseId)

        Expect.equal
            (CaseReference.format current.Snapshot.Reference)
            "CURRENT"
            "Navigation by Case ID shows the live reference"

        let! full = acceptedLookup service original.OperationId

        Expect.equal
            (CaseReference.format full.Reference)
            "On-Open"
            "Full-history receipt exposes the issued reference"

        match full.Snapshot with
        | SnapshotDisclosure.Disclosed { State = LiveCase.Active fields } ->
            Expect.equal
                (CaseReference.format fields.CaseReference)
                "On-Open"
                "Historical state shows the issued reference"
        | other -> failtestf "Expected a disclosed opening snapshot, received %A" other

        let! limited = acceptedLookup registrar original.OperationId

        Expect.equal
            (CaseReference.format limited.Reference)
            "CURRENT"
            "Current-only lookup uses the permitted current label"

        Expect.equal
            limited.Snapshot
            SnapshotDisclosure.Withheld
            "Current read access does not unlock the opening snapshot"

        let! replayed = Service.executeSubmission service original token
        let replayed = replayReceipt replayed |> caseReceipt

        Expect.equal
            (CaseReference.format replayed.Reference)
            "On-Open"
            "Authorized replay identifies the original issuance"

        let! replayed = Service.executeSubmission registrar original token
        let replayed = replayReceipt replayed |> caseReceipt

        Expect.equal
            replayed.Snapshot
            SnapshotDisclosure.Withheld
            "Current-only replay keeps the same disclosure boundary"

        Expect.equal
            (CaseReference.format replayed.Reference)
            "CURRENT"
            "Withheld replay retains the current label"

        let! scoped = grantRole 34 (GrantTarget.CaseReference "On-Open") true
        acceptedPeople scoped

        let! revoke = grantRole 35 GrantTarget.Installation false
        acceptedPeople revoke

        let! refused = Service.executeSubmission registrar (custom fixture 36 "SCOPED") token

        Expect.equal
            refused.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "A scoped registrar cannot open a case"

        let prior = custom fixture 37 "on-open"
        let! review = Service.previewSubmission service prior token

        match succeeds review.Outcome with
        | Previewed.Reviewed review ->
            let requested =
                match review.Target with
                | ReviewTarget.Opening requested -> value requested
                | other -> failtestf "Expected a pending opening, received %A" other

            Expect.equal
                requested
                (Some "on-open")
                "Preview preserves requested text even when reserved"
        | other -> failtestf "Expected a review without a reservation check, received %A" other

        let! conflict = Service.executeSubmission service prior token

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected(Refusal.Case CaseRefusal.ReferenceUnavailable))
            "A historic reservation refuses generically"

        let! _ = assigned service (request fixture 38 "CURRENT" 2L "case.erase" Map.empty)
        let! erased = acceptedLookup service original.OperationId

        Expect.equal
            (CaseReference.format erased.Reference)
            "CURRENT"
            "Erased lookup retains the permitted current label"

        Expect.equal
            erased.Snapshot
            SnapshotDisclosure.Erased
            "Erased receipt exposes no historic snapshot"

        let! erasedReplay = Service.executeSubmission service original token

        Expect.equal
            erasedReplay.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Erased inputs cannot be replayed"

        do! verify database
    }

let private reservationReads () =
    async {
        let! database = PostgresFixture.createWithStatementCounts () |> Async.AwaitTask

        try
            do!
                task {
                    let! service = caseWorker database.Fixture
                    let! _ = assigned service (custom database.Fixture 40 "OCCUPIED")
                    let conflicting = custom database.Fixture 41 "Occupied"

                    use! owner = database.SuperuserSource.OpenConnectionAsync()

                    let execute statement =
                        DatabaseSession.execute owner statement [||] token

                    let! _ = execute "CREATE EXTENSION pg_stat_statements"
                    let! _ = execute "SELECT public.pg_stat_statements_reset()"

                    let readReservationCalls () =
                        task {
                            let! rows =
                                DatabaseSession.rows
                                    owner
                                    "SELECT coalesce(sum(calls), 0)::bigint FROM public.pg_stat_statements WHERE lower(query) LIKE $1"
                                    [| box "%upper(natural_key collate%" |]
                                    token

                            return DatabaseSession.value<int64> 0 rows.Head
                        }

                    let! preview = Service.previewSubmission service conflicting token
                    succeeds preview.Outcome |> ignore
                    let! afterPreview = readReservationCalls ()

                    Expect.equal
                        afterPreview
                        0L
                        "Opening review never looks up a reference reservation"

                    let! commit = Service.executeSubmission service conflicting token

                    Expect.equal
                        commit.Outcome
                        (ExecuteResult.Rejected(Refusal.Case CaseRefusal.ReferenceUnavailable))
                        "Write-time reservation conflicts generically"

                    let! afterCommit = readReservationCalls ()

                    Expect.equal
                        afterCommit
                        1L
                        "The control is sensitive to the actual write-time reservation query"
                }
                |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testList
        "case reference issuance with PostgreSQL"
        [
            testCaseAsync
                "system-only policy, offer parity, pending preview and exact lookup"
                (run CaseReferencePolicy.SystemAssignedOnly systemPolicy)
            testCaseAsync
                "serialized allocation, collisions, rollback and replay"
                (run CaseReferencePolicy.CustomAllowed transactionalIssuance)
            testCaseAsync "preview makes zero permanent reservation reads" (reservationReads ())
            testCaseAsync
                "issued opening receipts preserve current, full and erased disclosure"
                (run CaseReferencePolicy.CustomAllowed receiptHistory)
        ]
