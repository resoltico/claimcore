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

let internal inspectCase fixture principal operation reference token =
    fixture.Store.Read(
        {
            ReadOperation.Work =
                (fun tx ->
                    ExecutionFacts.case
                        (core fixture principal)
                        tx
                        (Option.map CaseFixtures.operationId operation)
                        (CaseSelector.Reference reference)
                        None)
            CancellationToken = token
        }
    )

type internal AuthorityInspection =
    {
        Needed: AuthorityFact list
        OperationId: Guid
        Target: PrincipalKey
        CaseTargets: GrantTarget list
    }

let internal inspectTarget fixture principal (query: AuthorityInspection) token =
    fixture.Store.Read(
        {
            ReadOperation.Work =
                (fun tx ->
                    ExecutionFacts.target
                        (core fixture principal)
                        tx
                        query.Needed
                        (CaseFixtures.operationId query.OperationId)
                        query.Target
                        query.CaseTargets)
            CancellationToken = token
        }
    )

let internal inspectOperation fixture principal operation token =
    fixture.Store.Read(
        {
            ReadOperation.Work =
                (fun tx ->
                    task {
                        let! actor, entry, current =
                            ExecutionFacts.operation tx principal operation

                        let! maintenance = tx.Maintenance()
                        let! frontier = tx.ExpiredBefore()

                        return
                            {
                                Maintenance = maintenance
                                PaymentContext = CasePaymentContext.NoneRequired
                                CaseFacts.Assignee = None
                                ObservedAt = tx.ObservedAt
                                ExpiredBefore = frontier
                                Actor = actor
                                ExistingOperation = entry
                                Case =
                                    match current with
                                    | ReceiptCurrent.Case value
                                    | ReceiptCurrent.Work(value, _, _)
                                    | ReceiptCurrent.Reminder(value, _)
                                    | ReceiptCurrent.Note(value, _) -> value
                                    | ReceiptCurrent.Authority _ -> None
                            }
                    })
            CancellationToken = token
        }
    )

let internal inspectAuthority fixture principal token =
    fixture.Store.Read(
        {
            ReadOperation.Work =
                (fun tx ->
                    tx.Authority(
                        {
                            AuthorityQuery.Principal = principal
                            CaseId = None
                        }
                    ))
            CancellationToken = token
        }
    )

let internal id (fixture: Fixture) n = fixture.OperationIds.Id n


let internal request fixture n reference revision act : UnvalidatedCaseRequest =
    {
        OperationId = id fixture n
        CaseReference = reference
        ExpectedRevision = revision
        Act = act
    }

let internal actRequest fixture n reference revision act : UnvalidatedCaseRequest =
    {
        OperationId = id fixture n
        CaseReference = reference
        ExpectedRevision = revision
        Act = act
    }

let internal authorityRequest = Inputs.authorityRequest

let internal registration: RegistrationInput = Inputs.registration

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

let internal acceptedAuthority outcome =
    match accepted outcome with
    | { Subject = ReceiptSubject.Authority _ } -> ()
    | _ -> failtest "Expected accepted authority operation."

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
            AuthorityExecution.provisionStore
                (core fixture fixture.Owner)
                (id fixture 1)
                Inputs.ownerProfile
                []
                CancellationToken.None

        match result with
        | Ok(Verdict.Proceed _) -> ()
        | _ -> failtest "Initial owner must be accepted."

        return core fixture fixture.Owner
    }

let internal grant (actor: ServiceContext) operationId target (grant: AuthoredGrant) active =
    (Service.execute
        actor
        (OperationInput.Authority(
            authorityRequest
                operationId
                target
                ((if active then AuthorityAct.Grant else AuthorityAct.Revoke) grant)
        ))
        (CancellationToken.None))

let internal caseWorker fixture =
    task {
        let! owner = bootstrap fixture

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

            acceptedAuthority outcome

        return owner
    }
