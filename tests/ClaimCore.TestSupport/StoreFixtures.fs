module ClaimCore.TestSupport.StoreFixtures

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

let internal authorityRequest operationId target act : ClaimCore.Contract.AuthorityRequest =
    {
        OperationId = operationId
        Target = target
        Act = act
    }
    |> AuthorityRequests.toInput

let internal registration: RegistrationInput =
    {
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "LV"
        ClaimantName = "Synthetic Party"
        InsurerName = "Synthetic Insurer"
        ClaimedAmount = "1000.00"
        ClaimedCurrency = "EUR"
    }

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
    Handlers.context fixture.InstallationId fixture.Store fixture.Calendar principal

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
        | Ok(Decision.Proceed _) -> ()
        | _ -> failtest "Initial owner must be accepted."

        return core fixture fixture.Owner
    }

let internal grant (actor: HandlerContext) operationId target role scope active =
    (Handlers.call
        Api.operationExecute
        actor
        (OperationInput.Authority(
            authorityRequest
                operationId
                target
                ((if active then
                      AdmittedAuthorityAct.Grant
                  else
                      AdmittedAuthorityAct.Revoke) (
                    role,
                    scope
                ))
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
