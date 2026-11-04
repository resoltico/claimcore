module ClaimCore.IntegrationTests.ActorReceiptReadPostgresTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private id number =
    Guid.Parse(sprintf "e9adb05d-d7b0-430e-b2eb-%012d" number)

let private principal subject =
    PrincipalKey.human "https://pg-receipt-names.example/" subject
    |> Result.defaultWith (string >> failtest)

let private authority number target act =
    let kind, issuer, subject = PrincipalKey.parts target

    OperationInput.Authority
        {
            OperationId = id number
            Target =
                {
                    Kind = kind
                    Issuer = issuer
                    Subject = subject
                }
            Act = act
        }

let private execute service input =
    task {
        let! reply = Service.execute service input token

        return
            match reply.Outcome with
            | ExecuteResult.Accepted receipt -> receipt
            | other -> failtestf "%A" other
    }

let private named number target name =
    authority number target (AuthorityAct.Register { DisplayName = name; Contact = None })

let private target (receipt: OperationReceipt) =
    match receipt.Subject with
    | ReceiptSubject.Authority value -> value.TargetActor
    | other -> failtestf "%A" other

let private known receipt =
    target receipt |> AuthorityTargetDisclosure.actorId

let private status service operation =
    task {
        let! reply = Service.status service { OperationId = id operation } token

        return
            match reply.Outcome with
            | Outcome.Succeeded(OperationStatus.Accepted receipt) -> receipt
            | other -> failtestf "%A" other
    }

let private scenario (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = bootstrap fixture

        let! _ =
            execute
                owner
                (authority
                    1
                    fixture.Owner
                    (AuthorityAct.Grant
                        {
                            Role = Role.CaseHandler
                            Scope = GrantTarget.Installation
                        }))

        let! _ =
            execute
                owner
                (authority
                    2
                    fixture.Owner
                    (AuthorityAct.Grant
                        {
                            Role = Role.DataSteward
                            Scope = GrantTarget.Installation
                        }))

        let alice, bob, guardian = principal "alice", principal "bob", principal "guardian"
        let! aliceReceipt = execute owner (named 3 alice "Alice")
        let! bobReceipt = execute owner (named 4 bob "Bob")
        let! _ = execute owner (named 5 guardian "Guardian")

        let! _ =
            execute
                owner
                (authority
                    6
                    guardian
                    (AuthorityAct.Grant
                        {
                            Role = Role.Owner
                            Scope = GrantTarget.Installation
                        }))

        let guardianCore = core fixture guardian

        let! _ =
            execute
                owner
                (Inputs.caseOperation
                    {
                        OperationId = id 7
                        CaseReference = "PG-NAMED-WORK"
                        ExpectedRevision = 0L
                        Act = CaseAct.Open Inputs.registration
                    })

        for number, person in [ 8, alice; 9, bob ] do
            let! _ =
                execute
                    owner
                    (authority
                        number
                        person
                        (AuthorityAct.Grant
                            {
                                Role = Role.CaseHandler
                                Scope = GrantTarget.CaseReference "PG-NAMED-WORK"
                            }))

            ()

        let change number revision responsible =
            OperationInput.Work
                {
                    OperationId = id number
                    Target = CaseSelector.Reference "PG-NAMED-WORK"
                    ExpectedRevision = revision
                    Act =
                        WorkAct.UpdateWork
                            {
                                Responsible = WorkValue.Set(ActorId.format responsible)
                                Summary = WorkValue.Set "Private work summary"
                            }
                }

        let! _ = execute owner (change 10 0L (known aliceReceipt))
        let next = change 11 1L (known bobReceipt)
        let! preview = Service.preview owner next token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed(OperationReview.Work review)) ->
            Expect.equal
                (review.Before.Responsible |> Option.map _.DisplayName)
                (Some(DisplayName.Named(Inputs.name "Alice")))
                "The SQL batch retains the previous actor on the same case"

            Expect.equal
                (review.Proposed.Responsible |> Option.map _.DisplayName)
                (Some(DisplayName.Named(Inputs.name "Bob")))
                "The SQL batch retains the proposed actor independently"
        | other -> failtestf "%A" other

        let! _ = execute owner next

        let! saved =
            execute
                owner
                (authority
                    12
                    bob
                    (AuthorityAct.SetProfile
                        {
                            DisplayName = "Saved Bob"
                            Contact = None
                        }))

        match target saved with
        | AuthorityTargetDisclosure.Disclosed actor ->
            Expect.equal
                actor.DisplayName
                (DisplayName.Named(Inputs.name "Saved Bob"))
                "Accepted profile updates reflect the new saved name"
        | other -> failtestf "%A" other

        let! _ =
            execute
                guardianCore
                (authority
                    13
                    fixture.Owner
                    (AuthorityAct.Revoke
                        {
                            Role = Role.Owner
                            Scope = GrantTarget.Installation
                        }))

        let! _ =
            execute
                guardianCore
                (authority
                    14
                    bob
                    (AuthorityAct.SetProfile
                        {
                            DisplayName = "Current private Bob"
                            Contact = None
                        }))

        let! hidden = status owner 12

        Expect.equal
            (target hidden)
            (AuthorityTargetDisclosure.Withheld(known bobReceipt))
            "Own metadata survives without granting target profile access"

        let json =
            ReceiptCodecs.receipt.Write hidden
            |> WireWriter.bytes
            |> Text.Encoding.UTF8.GetString

        Expect.isFalse
            (json.Contains "Saved Bob" || json.Contains "Current private Bob")
            "Neither historical nor current target names escape the authority gate"

        let! _ =
            execute
                guardianCore
                (authority
                    15
                    fixture.Owner
                    (AuthorityAct.Grant
                        {
                            Role = Role.Owner
                            Scope = GrantTarget.Installation
                        }))

        let! visible = status owner 12

        match target visible with
        | AuthorityTargetDisclosure.Disclosed actor ->
            Expect.equal
                actor.DisplayName
                (DisplayName.Named(Inputs.name "Current private Bob"))
                "Regrant resolves the current account name"
        | other -> failtestf "%A" other

        let! _ = execute guardianCore (authority 16 bob AuthorityAct.EraseProfile)
        let! erased = status owner 12

        match target erased with
        | AuthorityTargetDisclosure.Disclosed actor ->
            Expect.equal
                actor.DisplayName
                DisplayName.Erased
                "Real erasure remains different from withheld details"
        | other -> failtestf "%A" other

        let! disabled = execute guardianCore (authority 17 bob AuthorityAct.Disable)

        match target disabled with
        | AuthorityTargetDisclosure.Disclosed actor ->
            Expect.equal
                actor.Availability
                ActorAvailability.Disabled
                "Disable receipts use current availability"
        | other -> failtestf "%A" other

        let! _ =
            execute
                owner
                (Inputs.caseOperation
                    {
                        OperationId = id 18
                        CaseReference = "PG-NAMED-WORK"
                        ExpectedRevision = 1L
                        Act = CaseAct.Bare BareAct.Erase
                    })

        let! observed = Service.observe owner { OperationId = id 10 } token

        match observed.Outcome with
        | Outcome.Succeeded {
                                Subject = ReceiptSubject.Work { Snapshot = WorkDisclosure.Erased }
                            } -> ()
        | other -> failtestf "Case erasure must dominate full history: %A" other

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testCaseAsync
        "actor receipt names preserve SQL relations and current disclosure gates"
        (run scenario)
