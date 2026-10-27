module ClaimCore.TestSupport.StoreFixtures

open ClaimCore.Protocol

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
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

let internal inspectCase fixture principal operation reference token =
    fixture.Store.Read(
        (fun tx -> ExecutionFacts.case tx fixture.InstallationId principal operation reference),
        token
    )

let internal inspectTarget fixture principal operation target reference token =
    fixture.Store.Read(
        (fun tx ->
            ExecutionFacts.target tx fixture.InstallationId principal operation target reference),
        token
    )

let internal inspectOperation fixture principal operation token =
    fixture.Store.Read(
        (fun tx ->
            task {
                let! actor, entry, current = ExecutionFacts.operation tx principal operation

                return
                    {
                        CaseFacts.ObservedAt = tx.ObservedAt
                        Actor = actor
                        ExistingOperation = entry
                        Case = current
                    }
            }),
        token
    )

let internal inspectAuthority fixture principal token =
    fixture.Store.Read((fun tx -> tx.Authority(principal, None)), token)

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
            receipt.OperationId
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

let internal core fixture principal =
    Service.create
        BuildIdentity.current.Version
        fixture.InstallationId
        fixture.Store
        fixture.Calendar
        principal

let internal bootstrap fixture =
    task {
        let! result =
            AuthorityExecution.provisionStore
                fixture.Store
                fixture.InstallationId
                (id 1)
                fixture.Owner
                CancellationToken.None

        match result with
        | Ok(Verdict.Proceed _) -> ()
        | _ -> failtest "Initial owner must be accepted."

        return core fixture fixture.Owner
    }

let internal grant (actor: ServiceContext) operationId target role scope active =
    (Core.execute
        actor
        (OperationInput.Authority(
            authorityRequest
                operationId
                target
                ((if active then AuthorityAct.Grant else AuthorityAct.Revoke)
                    { Role = role; Scope = scope })
        ))
        (CancellationToken.None))

let internal caseWorker fixture =
    task {
        let! owner = bootstrap fixture

        for operationId, role in [ id 2, Role.CaseHandler; id 900000, Role.PaymentRecorder ] do
            let! outcome =
                grant owner operationId fixture.Owner role GrantTarget.Installation true

            acceptedAuthority outcome

        return owner
    }
