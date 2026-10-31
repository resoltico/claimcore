module ClaimCore.TestSupport.StoreFixtures

open ClaimCore.TestSupport

open ClaimCore.Protocol

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

let internal resolve services input token =
    task {
        let! reply = Service.resolve services input token
        return reply.Outcome
    }

let internal observe services input token =
    task {
        let! reply = Service.observe services input token
        return reply.Outcome
    }

let internal inspectCase fixture principal operation reference token =
    fixture.Store.Read(
        {
            ReadOperation.Work =
                (fun tx ->
                    ExecutionFacts.case
                        (core fixture principal)
                        tx
                        (Option.map CaseFixtures.operationId operation)
                        reference)
            CancellationToken = token
        }
    )

type internal AuthorityInspection =
    {
        Needed: AuthorityFact list
        OperationId: Guid
        Target: PrincipalKey
        CaseReference: string option
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
                        query.CaseReference)
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

                        return
                            {
                                CaseFacts.ObservedAt = tx.ObservedAt
                                Actor = actor
                                ExistingOperation = entry
                                Case = current
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

let internal id n =
    Guid.Parse(sprintf "80000000-0000-4000-8000-%012d" n)


let internal request n reference revision act : UnvalidatedCaseRequest =
    {
        OperationId = id n
        CaseReference = reference
        ExpectedRevision = revision
        Act = act
    }

let internal actRequest n reference revision act : UnvalidatedCaseRequest =
    {
        OperationId = id n
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
    | _ -> failtest "Expected accepted operation."

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
    | SnapshotDisclosure.Disclosed((LiveCase.Active fields | LiveCase.Voided fields)) -> fields
    | _ -> failtest "Expected disclosed live fields."

let internal bootstrap fixture =
    task {
        let! result =
            AuthorityExecution.provisionStore
                (core fixture fixture.Owner)
                (id 1)
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

        for operationId, role in [ id 2, Role.CaseHandler; id 900000, Role.PaymentRecorder ] do
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
