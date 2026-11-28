module ClaimCore.IntegrationTests.ActorReceiptReadPostgresTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private principal subject =
    PrincipalKey.human "https://pg-receipt-names.example/" subject
    |> Result.defaultWith (string >> failtest)

let private people fixture number target taskName values =
    (personSubmission fixture.Store (id fixture number) target None taskName values)
        .GetAwaiter()
        .GetResult()

let private execute service input =
    task {
        let! reply = Service.executeSubmission service input token

        return
            match reply.Outcome with
            | ExecuteResult.Accepted receipt -> receipt
            | other -> failtestf "%A" other
    }

let private named fixture number target name =
    people
        (fixture)
        (number)
        (target)
        "people.register"
        (ClaimCore.TestSupport.Submissions.profile (
            {
                DisplayName = name
                Contact = None
                DigestEmail = None
            }
        ))

let private target (receipt: OperationReceipt) =
    match receipt.Subject with
    | ReceiptSubject.People value -> value.TargetActor
    | other -> failtestf "%A" other

let private known receipt =
    target receipt |> PeopleTargetDisclosure.actorId

let private status fixture service operation =
    task {
        let! reply =
            Service.operationGet service { OperationId = id fixture operation } token

        return
            match reply.Outcome with
            | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, true)) -> receipt
            | other -> failtestf "%A" other
    }

let private scenario (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = bootstrap fixture

        let! _ =
            execute
                owner
                (people
                    (fixture)
                    (101)
                    (fixture.Owner)
                    "people.grant"
                    (ClaimCore.TestSupport.Submissions.grant (
                        {
                            Role = Role.CaseHandler
                            Scope = GrantTarget.Installation
                        }
                    )))

        let! _ =
            execute
                owner
                (people
                    (fixture)
                    (2)
                    (fixture.Owner)
                    "people.grant"
                    (ClaimCore.TestSupport.Submissions.grant (
                        {
                            Role = Role.DataSteward
                            Scope = GrantTarget.Installation
                        }
                    )))

        let alice, bob, guardian = principal "alice", principal "bob", principal "guardian"
        let! aliceReceipt = execute owner (named fixture 3 alice "Alice")
        let! bobReceipt = execute owner (named fixture 4 bob "Bob")
        let! _ = execute owner (named fixture 5 guardian "Guardian")

        let! _ =
            execute
                owner
                (people
                    (fixture)
                    (6)
                    (guardian)
                    "people.grant"
                    (ClaimCore.TestSupport.Submissions.grant (
                        {
                            Role = Role.Owner
                            Scope = GrantTarget.Installation
                        }
                    )))

        let guardianCore = core fixture guardian

        let! _ =
            execute
                owner
                ((ClaimCore.TestSupport.Submissions.createRaw
                    "case.open"
                    (id fixture 7)
                    (ClaimCore.Application.Contracts.OperationBinding.CaseNew(0L))
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (DomainInputs.registration)
                    ))))

        for number, person in [ 8, alice; 9, bob ] do
            let! _ =
                execute
                    owner
                    (people
                        (fixture)
                        (number)
                        (person)
                        "people.grant"
                        (ClaimCore.TestSupport.Submissions.grant (
                            {
                                Role = Role.CaseHandler
                                Scope = GrantTarget.CaseId(CaseFixtures.caseId (id fixture 7))
                            }
                        )))

            ()

        let change number revision responsible =
            (ClaimCore.TestSupport.Submissions.createRaw)
                "work.update"
                (id fixture number)
                (ClaimCore.Application.Contracts.OperationBinding.Work(
                    (CaseSelector.Id(CaseFixtures.caseId (id fixture 7))),
                    (revision)
                ))
                (ClaimCore.TestSupport.Submissions.work (
                    {
                        Responsible = WorkValue.Set(ActorId.format responsible)
                        Summary = WorkValue.Set "Private work summary"
                    }
                ))

        let! _ = execute owner (change 10 0L (known aliceReceipt))
        let next = change 11 1L (known bobReceipt)
        let! preview = Service.previewSubmission owner next token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed review) ->
            let responsibility =
                match review.Changes.Head.Description with
                | ClaimCore.Domain.Changes.PartDescription.Work description ->
                    description.Responsible |> Option.get
                | _ -> failtest "Work description required"

            let label selected =
                selected
                |> Option.bind (fun id ->
                    review.References.Actors |> List.tryFind (fun actor -> actor.ActorId = id))
                |> Option.map _.DisplayName

            Expect.equal
                (label responsibility.Before)
                (Some(DisplayName.Named(DomainInputs.name "Alice")))
                "The SQL batch retains the previous actor on the same case"

            Expect.equal
                (label responsibility.After)
                (Some(DisplayName.Named(DomainInputs.name "Bob")))
                "The SQL batch retains the proposed actor independently"
        | other -> failtestf "%A" other

        let! _ = execute owner next

        let! _ =
            execute
                owner
                (people
                    (fixture)
                    (19)
                    (alice)
                    "people.setProfile"
                    (ClaimCore.TestSupport.Submissions.profile (
                        {
                            DisplayName = "Current Alice"
                            DigestEmail = None
                            Contact = None
                        }
                    )))

        let! historical = Service.operationGet owner { OperationId = id fixture 10 } token

        match historical.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Accepted({
                                                               Subject = ReceiptSubject.Work {
                                                                                                 Snapshot = WorkDisclosure.Disclosed view
                                                                                             }
                                                           },
                                                           _)) ->
            Expect.equal
                (view.Responsible |> Option.map _.DisplayName)
                (Some(DisplayName.Named(DomainInputs.name "Current Alice")))
                "Historical work resolves the current saved name"
        | other -> failtestf "%A" other

        let reader = principal "reader"
        let! _ = execute owner (named fixture 20 reader "Reader")

        let! _ =
            execute
                owner
                (people
                    (fixture)
                    (21)
                    (reader)
                    "people.grant"
                    (ClaimCore.TestSupport.Submissions.grant (
                        {
                            Role = Role.CaseReader
                            Scope = GrantTarget.CaseId(CaseFixtures.caseId (id fixture 7))
                        }
                    )))

        let! limited =
            Service.operationGet (core fixture reader) { OperationId = id fixture 10 } token

        match limited.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Accepted({
                                                               Subject = ReceiptSubject.Work {
                                                                                                 Snapshot = WorkDisclosure.Withheld
                                                                                             }
                                                           },
                                                           _)) -> ()
        | other -> failtestf "Old work must remain withheld without full history rights: %A" other

        let! saved =
            execute
                owner
                (people
                    (fixture)
                    (12)
                    (bob)
                    "people.setProfile"
                    (ClaimCore.TestSupport.Submissions.profile (
                        {
                            DisplayName = "Saved Bob"
                            DigestEmail = None
                            Contact = None
                        }
                    )))

        match target saved with
        | PeopleTargetDisclosure.Disclosed actor ->
            Expect.equal
                actor.DisplayName
                (DisplayName.Named(DomainInputs.name "Saved Bob"))
                "Accepted profile updates reflect the new saved name"
        | other -> failtestf "%A" other

        let! _ =
            execute
                guardianCore
                (people
                    (fixture)
                    (13)
                    (fixture.Owner)
                    "people.revoke"
                    (ClaimCore.TestSupport.Submissions.grant (
                        {
                            Role = Role.Owner
                            Scope = GrantTarget.Installation
                        }
                    )))

        let! _ =
            execute
                guardianCore
                (people
                    (fixture)
                    (14)
                    (bob)
                    "people.setProfile"
                    (ClaimCore.TestSupport.Submissions.profile (
                        {
                            DisplayName = "Current private Bob"
                            DigestEmail = None
                            Contact = None
                        }
                    )))

        let! hidden = status fixture owner 12

        Expect.equal
            (target hidden)
            (PeopleTargetDisclosure.Withheld(known bobReceipt))
            "Own metadata survives without granting target profile access"

        let json =
            ReceiptCodecs.receipt.Write hidden
            |> WireWriter.bytes
            |> Text.Encoding.UTF8.GetString

        Expect.isFalse
            (json.Contains "Saved Bob" || json.Contains "Current private Bob")
            "Neither historical nor current target names escape the people fixture gate"

        let! _ =
            execute
                guardianCore
                (people
                    (fixture)
                    (15)
                    (fixture.Owner)
                    "people.grant"
                    (ClaimCore.TestSupport.Submissions.grant (
                        {
                            Role = Role.Owner
                            Scope = GrantTarget.Installation
                        }
                    )))

        let! visible = status fixture owner 12

        match target visible with
        | PeopleTargetDisclosure.Disclosed actor ->
            Expect.equal
                actor.DisplayName
                (DisplayName.Named(DomainInputs.name "Current private Bob"))
                "Regrant resolves the current account name"
        | other -> failtestf "%A" other

        let! _ =
            execute guardianCore (people (fixture) (16) (bob) "people.eraseProfile" (Map.empty))

        let! erased = status fixture owner 12

        match target erased with
        | PeopleTargetDisclosure.Disclosed actor ->
            Expect.equal
                actor.DisplayName
                DisplayName.Erased
                "Real erasure remains different from withheld details"
        | other -> failtestf "%A" other

        let! disabled =
            execute guardianCore (people (fixture) (17) (bob) "people.disable" (Map.empty))

        match target disabled with
        | PeopleTargetDisclosure.Disclosed actor ->
            Expect.equal
                actor.Availability
                ActorAvailability.Disabled
                "Disable receipts use current availability"
        | other -> failtestf "%A" other

        let! _ =
            execute
                owner
                ((ClaimCore.TestSupport.Submissions.createRaw
                    "case.erase"
                    (id fixture 18)
                    (ClaimCore.Application.Contracts.OperationBinding.Case(
                        CaseSelector.Id(CaseFixtures.caseId (id fixture 7)),
                        (1L),
                        None,
                        (Some(
                            reviewedErasureToken
                                fixture.Store
                                fixture.Owner
                                (CaseSelector.Id(CaseFixtures.caseId (id fixture 7)))
                        ))
                    ))
                    (Map.empty)))

        let! observed = Service.operationGet owner { OperationId = id fixture 10 } token

        match observed.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Accepted({
                                                               Subject = ReceiptSubject.Work {
                                                                                                 Snapshot = WorkDisclosure.Erased
                                                                                             }
                                                           },
                                                           _)) -> ()
        | other -> failtestf "Case erasure must dominate full history: %A" other

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testCaseAsync
        "actor receipt names preserve SQL relations and current disclosure gates"
        (run scenario)
