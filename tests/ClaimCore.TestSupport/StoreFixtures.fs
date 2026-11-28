module ClaimCore.TestSupport.StoreFixtures

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification

[<NoEquality; NoComparison>]
type internal Fixture =
    {
        Store: IStore
        OperationIds: OperationIds.Scope
        InstallationId: Guid
        Owner: PrincipalKey
        Calendar: IBusinessCalendar
        Dispose: unit -> unit
    }

let internal core fixture principal =
    Service.create
        BuildIdentity.current.Version
        fixture.InstallationId
        fixture.Store
        fixture.Calendar
        principal

let internal inspectPeople fixture principal token =
    fixture.Store.Read(
        {
            ReadOperation.Work = (fun tx -> AccessReads.access tx (principal) (None))
            CancellationToken = token
        }
    )

let internal humanPrincipalKey issuer subject =
    PrincipalKey.human issuer subject
    |> Result.defaultWith (fun error -> failtestf "Fixture principal: %A" error)
    |> PrincipalKey.naturalKey

let internal id (fixture: Fixture) n = fixture.OperationIds.Id n


/// Fixture acquisition freezes reviewed metadata at construction, never during execution or retry.
let private readFixture (store: IStore) work =
    store.Read
        {
            Work = work
            CancellationToken = CancellationToken.None
        }
    |> _.GetAwaiter().GetResult()
    |> Result.defaultWith (fun failure -> failtestf "Fixture review failed: %A" failure)

let internal reviewedErasureToken store principal target =
    readFixture store (fun tx ->
        task {
            let! current =
                match target with
                | CaseSelector.Id id -> FamilyReads.findCase tx id
                | CaseSelector.Reference reference -> FamilyReads.findCaseByReference tx reference

            let! access = ExecutionEnvironment.requester tx principal

            match current, access.Actor with
            | Some current, Some actor ->
                let! position = FamilyReads.caseFamilySequence tx current.CaseId

                return!
                    CaseFamilyAcquisition.issueErasureReview
                        tx
                        current.CaseId
                        actor.ActorId
                        position
            | _ -> return "unavailable-review"
        })

let internal caseTarget (fixture: Fixture) name =
    match fixture.OperationIds.Case name with
    | Some operation -> CaseSelector.Id(CaseId.create operation |> Option.get)
    | None -> CaseSelector.Reference(CaseReference.format (DomainInputs.reference name))

let internal caseAlias (fixture: Fixture) caseId =
    fixture.OperationIds.CaseName(CaseId.value caseId)
    |> Option.defaultWith (fun () -> failtest "The case has no fixture label.")

let internal issuedCaseReference (fixture: Fixture) name =
    readFixture fixture.Store (fun tx ->
        match caseTarget fixture name with
        | CaseSelector.Id id -> FamilyReads.findCase tx id
        | CaseSelector.Reference reference -> FamilyReads.findCaseByReference tx reference)
    |> Option.map (_.Snapshot.Reference >> CaseReference.format)
    |> Option.defaultWith (fun () -> failtest "The case fixture has no issued reference.")

let internal caseGrant fixture name =
    match caseTarget fixture name with
    | CaseSelector.Id id -> GrantTarget.CaseId id
    | CaseSelector.Reference reference -> GrantTarget.CaseReference reference

let internal requestAs
    fixture
    operation
    reference
    revision
    task
    (values: Map<TaskInputId, SubmissionValue>)
    =
    let declaration =
        ClaimCore.Domain.Tasks.Registry.findDeclaration (TaskId.ofLiteral task)
        |> Option.get

    let target = caseTarget fixture reference
    let paymentInput = Submissions.input InputTarget.PaymentId

    let binding, values =
        match TaskDeclaration.bindingShape declaration with
        | BindingShape.CaseNew ->
            fixture.OperationIds.DescribeCase(reference, operation)
            OperationBinding.CaseNew revision, values
        | BindingShape.CaseErase ->
            OperationBinding.Case(
                target,
                revision,
                None,
                Some(reviewedErasureToken fixture.Store fixture.Owner target)
            ),
            values
        | BindingShape.CasePayment ->
            let payment =
                values[paymentInput]
                |> function
                    | SubmissionValue.Text text -> Guid.Parse text
                    | _ -> failtest "Payment fixture selection required"

            OperationBinding.Case(target, revision, Some payment, None),
            Map.remove paymentInput values
        | BindingShape.Case -> OperationBinding.Case(target, revision, None, None), values
        | _ -> invalidArg (nameof task) "Supply an explicit row or person binding."

    Submissions.createRaw task operation binding values

let internal request fixture n = requestAs fixture (id fixture n)

/// Fixture setup reads a ready actor identity; explicit stale revisions remain caller-supplied.
let internal personSubmission (store: IStore) operation target expected taskName values =
    task {
        let declaration =
            ClaimCore.Domain.Tasks.Registry.findDeclaration (TaskId.ofLiteral taskName)
            |> Option.get

        let! binding =
            task {
                if declaration.Creates then
                    return OperationBinding.People(None, Option.defaultValue 0L expected)
                else
                    let! result =
                        store.Read
                            {
                                CancellationToken = CancellationToken.None
                                Work =
                                    fun tx ->
                                        task {
                                            let! rows =
                                                DatabaseSession.rows
                                                    tx.Connection
                                                    "SELECT actor_id,revision FROM claimcore.actors WHERE principal_key=$1"
                                                    [| box (PrincipalKey.naturalKey target) |]
                                                    tx.CancellationToken

                                            return
                                                rows
                                                |> List.tryHead
                                                |> Option.map (fun row ->
                                                    DatabaseSession.value<Guid> 0 row,
                                                    DatabaseSession.value<int64> 1 row)
                                        }
                            }

                    let current =
                        result
                        |> Result.defaultWith (fun error ->
                            failtestf "Fixture actor binding read failed: %A" error)

                    let identity, revision = current |> Option.defaultValue (operation, 1L)

                    return
                        OperationBinding.People(
                            Some identity,
                            Option.defaultValue revision expected
                        )
            }

        let authored =
            if declaration.Creates then
                Map.fold
                    (fun values key value -> Map.add key value values)
                    values
                    (Submissions.principal target)
            else
                values

        return Submissions.createRaw taskName operation binding authored
    }

let internal registration: RegistrationInput = DomainInputs.registration

let internal accepted (reply: ExecuteReply) =
    match reply.Outcome with
    | ExecuteResult.Accepted receipt ->
        Expect.equal
            reply.OperationId
            (OperationId.value receipt.OperationId)
            "Accepted reply echoes its operation identity"

        receipt
    | other -> failtestf "Expected accepted operation, received %A." other

/// Acceptance metadata stays historical; the basis describes the current authorized read.
let internal expectReceipt (actual: OperationReceipt) (expected: OperationReceipt) message =
    Expect.equal
        (actual.OperationId, actual.Position, actual.Subject, actual.RecordedAt, actual.RecordedBy)
        (expected.OperationId,
         expected.Position,
         expected.Subject,
         expected.RecordedAt,
         expected.RecordedBy)
        message

    Expect.isGreaterThanOrEqual
        actual.Basis.ObservedAt
        expected.Basis.ObservedAt
        "The later receipt cannot report an earlier database observation"

    Expect.equal actual.Basis.TimeZoneId expected.Basis.TimeZoneId "Installation zone is retained"

    let observedDate =
        TimeZoneInfo.ConvertTime(
            actual.Basis.ObservedAt,
            TimeZoneInfo.FindSystemTimeZoneById expected.Basis.TimeZoneId
        )
        |> fun instant -> DateOnly.FromDateTime instant.DateTime

    Expect.equal
        actual.Basis.BusinessDate
        observedDate
        "Business date belongs to the current observation"

let internal replayReceipt (reply: ExecuteReply) =
    match reply.Outcome with
    | ExecuteResult.AlreadyAccepted receipt -> receipt
    | other -> failtestf "Expected already accepted operation, received %A." other

let internal acceptedPeople outcome =
    match accepted outcome with
    | { Subject = ReceiptSubject.People _ } -> ()
    | _ -> failtest "Expected accepted people operation."

let internal caseReceipt (receipt: OperationReceipt) =
    match receipt.Subject with
    | ReceiptSubject.Case value -> value
    | _ -> failtest "Expected case receipt."

let internal disclosedFields receipt =
    match (caseReceipt receipt).Snapshot with
    | SnapshotDisclosure.Disclosed {
                                       State = (LiveCase.Active fields | LiveCase.Voided fields)
                                   } -> fields
    | _ -> failtest "Expected disclosed live fields."

let internal bootstrap fixture =
    task {
        let! result =
            Service.provisionOwner
                (core fixture fixture.Owner)
                (id fixture 1)
                DomainInputs.ownerProfile
                []
                CancellationToken.None

        match result with
        | Ok(Verdict.Proceed _) -> ()
        | other -> failtestf "Initial owner must be accepted: %A." other

        return core fixture fixture.Owner
    }

let internal grant (actor: ServiceContext) operationId target (grant: GrantInput) active =
    task {
        let! observed =
            actor.Store.Read
                {
                    Work = (fun tx -> PersonAcquisition.principal tx target)
                    CancellationToken = CancellationToken.None
                }

        let current =
            observed
            |> Result.defaultWith (fun error -> failtestf "Setup review failed: %A" error)
            |> Option.defaultWith (fun () -> failtest "Grant target is absent.")

        let input =
            Submissions.create
                (if active then "people.grant" else "people.revoke")
                operationId
                (Binding.People(current.Id, current.Revision))
                (Submissions.grant grant)

        return! Service.executeSubmission actor input CancellationToken.None
    }

let internal grantCaseWork fixture owner =
    task {
        for operationId, role in
            [ id fixture 2, Role.CaseHandler; id fixture 900000, Role.PaymentRecorder ] do
            let! outcome =
                grant
                    owner
                    operationId
                    fixture.Owner
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedPeople outcome

    }

let internal caseWorker fixture =
    task {
        let! owner = bootstrap fixture
        do! grantCaseWork fixture owner
        return owner
    }
