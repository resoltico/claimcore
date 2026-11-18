module ClaimCore.IntegrationTests.DiagnosticEntryEvidence

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures
open ClaimCore.IntegrationTests.DiagnosticWitnesses

let entriesEvidence () =
    task {
        let! _, fixture = fixture ()

        use _owned =
            { new IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture
        let target = CaseSelector.Reference "ENTRY-WITNESSES"
        let results = ResizeArray<_>()
        let mutable number = 7000

        let next () =
            number <- number + 1
            id fixture number

        let case revision taskName values =
            let submission =
                request fixture 0 "ENTRY-WITNESSES" revision taskName values
                |> Submissions.withOperationId (next ())

            Service.executeSubmission actor submission ct

        let payment amount currency =
            "case.recordPayment",
            Submissions.fields (
                PaymentInput.values (
                    DomainInputs.paymentFrom
                        [
                            BusinessField.PaymentDate, "2026-08-20"
                            BusinessField.PaidAmount, amount
                            BusinessField.PaidCurrency, currency
                        ]
                )
            )

        let remove (identity: Guid) =
            "case.removePayment",
            Map.ofList
                [
                    Submissions.input InputTarget.PaymentId,
                    SubmissionValue.Text(identity.ToString("D"))
                ]

        // Raw authoring errors are admitted refusals, not malformed transport or typed grants.
        for selection in [ [ TaskInputId.CaseId, SubmissionValue.Text "not-a-case-id" ]; [] ] do
            let envelope =
                {
                    SubmissionEnvelope.OperationId = next ()
                    Task = (TaskId.ofLiteral "people.grant")
                    Binding = OperationBinding.People(Some(id fixture 7998), 1L)
                    Inputs =
                        Map.ofList (
                            [
                                TaskInputId.Target InputTarget.Role,
                                SubmissionValue.Text "CASE_READER"
                                TaskInputId.Scope, SubmissionValue.Text "CASE"
                            ]
                            @ selection
                        )
                }

            let submission =
                OperationSubmission.admit envelope
                |> Result.defaultWith (fun () -> failtest "Well-typed grant authoring is admitted")

            let! reply = Service.executeSubmission actor submission ct
            results.Add(refused reply)

        let! opened =
            case
                (0L)
                "case.open"
                (ClaimCore.TestSupport.Submissions.fields (RegistrationInput.values (registration)))

        accepted opened |> ignore

        for field in [ BusinessField.PolicyNumber; BusinessField.InsurerClaimReference ] do
            let patch =
                RegistrationUpdateInput.create [ field, Some "" ]
                |> Result.defaultWith invalidOp

            let! reply =
                case
                    (1L)
                    "case.amendRegistration"
                    (ClaimCore.TestSupport.Submissions.updates (
                        RegistrationUpdateInput.values (patch)
                    ))

            results.Add(refused reply)

        for taskName, selected, values in
            [
                "note.add",
                None,
                Map.ofList [ Submissions.input InputTarget.NoteText, SubmissionValue.Text "" ]
                "note.remove", Some Guid.Empty, Map.empty
            ] do
            let! reply =
                Service.executeSubmission
                    actor
                    (Submissions.createRaw
                        taskName
                        (next ())
                        (OperationBinding.Note(target, selected))
                        values)
                    ct

            results.Add(refused reply)

        let! decided =
            case
                (1L)
                "case.decide"
                (ClaimCore.TestSupport.Submissions.fields (DecisionInput.values (decision)))

        accepted decided |> ignore

        for taskName, values in
            [ payment "0" "EUR"; payment "bad" "EUR"; payment "1" "bad"; remove Guid.Empty ] do
            let! reply = case 2L taskName values
            results.Add(refused reply)

        let! paid = case 2L "case.recordPayment" (snd (payment "100" "EUR"))
        accepted paid |> ignore

        let taskName, values = remove (id fixture 7999)
        let! absent = case 3L taskName values
        results.Add(refused absent)

        // Keep an outstanding balance while crossing the retired fifty-payment boundary.
        for revision in 3L .. 51L do
            let! reply = case revision "case.recordPayment" (snd (payment "1" "EUR"))
            accepted reply |> ignore

        let! fiftyFirst = case 52L "case.recordPayment" (snd (payment "1" "EUR"))
        accepted fiftyFirst |> ignore

        let! removed =
            case
                (53L)
                "case.removePayment"
                (Map.ofList
                    [
                        TaskInputId.Target InputTarget.PaymentId,
                        ClaimCore.Application.Contracts.SubmissionValue.Text(
                            ((OperationId.value (accepted paid).OperationId)).ToString("D")
                        )
                    ])

        let afterRemoval = accepted removed |> caseReceipt

        Expect.equal
            (Revision.value afterRemoval.Revision)
            54L
            "The fifty-first payment and removal each advance revision"

        let! recordedAgain = case 54L "case.recordPayment" (snd (payment "1" "EUR"))
        let restored = accepted recordedAgain |> caseReceipt

        match restored.Snapshot with
        | SnapshotDisclosure.Disclosed live ->
            let fields = LiveCase.toState live.State |> CaseState.fields |> Option.get
            Expect.equal fields.Finance.PaymentCount 51L "Recording remains available above fifty"

            Expect.equal
                (CaseFixtures.netPaid "EUR" fields.Finance)
                "51"
                "Removing 100 from 150 then adding 1 yields fifty-one euros"
        | _ -> failtest "The authorized payment witness requires a disclosed result"

        return List.ofSeq results
    }

// The safe-integer boundary belongs to pure Domain admission. A real accepted history cannot
// be populated to that count in a test; no corrupted PostgreSQL projection is used as evidence.
let technicalCountEvidence () =
    let valid value =
        Result.defaultWith (fun error -> failtestf "Technical boundary fixture: %A" error) value

    let decision =
        DomainInputs.decisionWith [ BusinessField.PayableAmount, "999999999999999999" ] decision
        |> DomainInputs.admittedDecision

    let currency = Currency.parse "EUR" |> valid

    let amounts =
        {
            CurrencyPaymentTotals.Paid = DecimalTotal.parse "1000000000000" |> valid
            Returned = DecimalTotal.zero
        }

    let finance =
        FinancialPosition.create
            (Some decision)
            (Map.ofList [ currency, amounts ])
            SafeInteger.Maximum
        |> valid

    let payment =
        DomainInputs.paymentFrom
            [
                BusinessField.PaymentDate, "2026-08-20"
                BusinessField.PaidAmount, "0.0001"
                BusinessField.PaidCurrency, "EUR"
            ]
        |> DomainInputs.admittedPayment

    Expect.equal
        (FinancialPosition.add
            (PaymentEntry.recorded
                (CaseFixtures.operationId (Guid.Parse "10000000-0000-7000-8000-000000000001"))
                payment)
            finance)
        (Error FinancialPositionIssue.CountOutOfRange)
        "The financial owner refuses safe-integer saturation without constructing an impossible-sized family"
    // This boundary cannot be reached in a finite PostgreSQL fixture. Verify its public
    // diagnostic independently, without claiming that a forged family reached task admission.
    // The exact-range case is specified at the financial owner. Only its protocol mapping is tested here.
    diagnostic (
        Refusal.invalidInput (InputTarget.Group RecordGroup.Payment, Violation.PaymentCountLimit)
    )
    @ diagnostic (
        Refusal.invalidInput (
            InputTarget.Field BusinessField.PaidAmount,
            Violation.PaymentTotalRange
        )
    )

[<Tests>]
let technicalCount =
    testCase
        "financial count saturation and its public diagnostic are independently checked"
        (fun () ->
            Expect.equal
                (technicalCountEvidence ())
                [
                    "INVALID_INPUT", None
                    "PAYMENT_COUNT_LIMIT", Some "payment"
                    "INVALID_INPUT", None
                    "PAYMENT_TOTAL_RANGE", Some "paidAmount"
                ]
                "The core integer boundary crosses the public diagnostic codec")

let associationEvidence () =
    task {
        let! _, fixture = fixture ()

        use _owned =
            { new IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture
        let witnesses = ResizeArray<_>()
        let mutable sequence = 8500

        let next () =
            sequence <- sequence + 1
            id fixture sequence

        let fields values =
            values
            |> List.map (fun (input, value) -> Submissions.input input, SubmissionValue.Text value)
            |> Map.ofList

        let move taskName binding values =
            Service.executeSubmission
                actor
                (Submissions.createRaw taskName (next ()) binding values)
                ct

        let openCase reference additional =
            let values =
                additional
                |> Map.add
                    (Submissions.field BusinessField.CaseReference)
                    (SubmissionValue.Text reference)

            move "case.open" (OperationBinding.CaseNew 0L) values

        let source = CaseSelector.Reference "DIAG-ASSOCIATIONS"
        let! opened = openCase "DIAG-ASSOCIATIONS" Map.empty
        accepted opened |> ignore

        let basicParty =
            fields [ InputTarget.PartyRole, "OTHER"; InputTarget.PartyName, "Synthetic witness" ]

        for party in
            [
                fields
                    [ InputTarget.PartyRole, "UNKNOWN"; InputTarget.PartyName, "Synthetic witness" ]
                fields [ InputTarget.PartyRole, "OTHER" ]
                fields [ InputTarget.PartyRole, "OTHER"; InputTarget.PartyDetails, "" ]
            ] do
            let! rejected = move "party.add" (OperationBinding.Party(source, None)) party
            witnesses.Add(refused rejected)

        let! malformed =
            move "party.update" (OperationBinding.Party(source, Some(Guid.Empty, 1L))) basicParty

        witnesses.Add(refused malformed)

        let! created = move "party.add" (OperationBinding.Party(source, None)) basicParty
        let createdId = OperationId.value (accepted created).OperationId

        let! badRevision =
            move "party.update" (OperationBinding.Party(source, Some(createdId, -1L))) basicParty

        witnesses.Add(refused badRevision)

        let partyRows count =
            Map.ofList
                [
                    TaskCollectionId.input TaskCollectionId.Parties,
                    SubmissionValue.Rows(List.replicate count basicParty)
                ]

        let! excess = openCase "DIAG-TOO-MANY-PARTIES" (partyRows 21)
        witnesses.Add(refused excess)

        let! full = openCase "DIAG-PARTIES-FULL" (partyRows 20)
        accepted full |> ignore

        let! over =
            move
                "party.add"
                (OperationBinding.Party(CaseSelector.Reference "DIAG-PARTIES-FULL", None))
                basicParty

        witnesses.Add(refused over)

        let link values =
            move "link.add" (OperationBinding.Link(source, None)) (fields values)

        let target name =
            [ InputTarget.LinkTargetReference, name ]

        for invalid in
            [
                target "not a reference"
                [ InputTarget.LinkTargetId, "not-a-case-id" ]
                target "DIAG-ASSOCIATIONS"
            ] do
            let! rejected = link invalid
            witnesses.Add(refused rejected)

        let! malformedLink =
            move "link.remove" (OperationBinding.Link(source, Some Guid.Empty)) Map.empty

        witnesses.Add(refused malformedLink)

        for number in 1..21 do
            let! opened = openCase ($"DIAG-LINK-{number}") Map.empty
            accepted opened |> ignore

        let! invalidNote = link (target "DIAG-LINK-1" @ [ InputTarget.LinkNote, "" ])
        witnesses.Add(refused invalidNote)
        let! first = link (target "DIAG-LINK-1")
        accepted first |> ignore
        let! duplicate = link (target "DIAG-LINK-1")
        witnesses.Add(refused duplicate)

        for number in 2..20 do
            let! linked = link (target ($"DIAG-LINK-{number}"))
            accepted linked |> ignore

        let! limit = link (target "DIAG-LINK-21")
        witnesses.Add(refused limit)
        return List.ofSeq witnesses
    }

let authoredIdentityAndClosureEvidence () =
    task {
        let! _, fixture = fixture ()

        use _owned =
            { new IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture
        let witnesses = ResizeArray<_>()

        let field key text =
            Submissions.field key, SubmissionValue.Text text

        let input key text =
            Submissions.input key, SubmissionValue.Text text

        let requestCase number taskName values =
            Service.executeSubmission
                actor
                (request fixture number "DIAG-AUTHORED" 1L taskName values)
                ct

        let! opened =
            Service.executeSubmission
                actor
                (request fixture 8600 "DIAG-AUTHORED" 0L "case.open" Map.empty)
                ct

        accepted opened |> ignore

        let money =
            Submissions.fields (
                PaymentInput.values (
                    DomainInputs.paymentFrom
                        [
                            BusinessField.PaymentDate, "2026-08-20"
                            BusinessField.PaidAmount, "1"
                            BusinessField.PaidCurrency, "EUR"
                        ]
                )
            )

        for number, taskName, values in
            [
                8601,
                "case.changeReference",
                Map.ofList [ input InputTarget.NewCaseReference "invalid reference" ]
                8602, "case.close", Map.ofList [ field BusinessField.Outcome "UNKNOWN" ]
                8603,
                "case.close",
                Map.ofList
                    [ field BusinessField.Outcome "OTHER"; field BusinessField.ClosingNote "" ]
                8604,
                "case.decide",
                Submissions.fields (DecisionInput.values decision)
                |> Map.add (Submissions.field BusinessField.DecisionBasis) (SubmissionValue.Text "")
                8605,
                "case.recordPayment",
                money
                |> Map.add
                    (Submissions.input InputTarget.PaymentDirection)
                    (SubmissionValue.Text "UNKNOWN")
                8606,
                "case.recordPayment",
                money
                |> Map.add
                    (Submissions.input InputTarget.PaymentReference)
                    (SubmissionValue.Text "")
                8607,
                "case.recordPayment",
                money
                |> Map.add
                    (Submissions.input InputTarget.PaymentPayee)
                    (SubmissionValue.Text((id fixture 8612).ToString("D")))
            ] do
            let! rejected = requestCase number taskName values
            witnesses.Add(refused rejected)

        let principal =
            PrincipalKey.human "https://issuer.example/" "reserved-diagnostic"
            |> Result.defaultWith (fun issue -> failtestf "Synthetic principal: %A" issue)

        let person number original taskName values =
            task {
                let! submission =
                    personSubmission fixture.Store (id fixture number) original None taskName values

                return! Service.executeSubmission actor submission ct
            }

        let! registered =
            person 8608 principal "people.register" (Submissions.profile DomainInputs.actorProfile)

        acceptedPeople registered

        let identity principal =
            Submissions.principal principal
            |> Map.remove (Submissions.input InputTarget.PrincipalKind)

        let! reserved =
            person 8609 fixture.Owner "people.changeSignInIdentity" (identity principal)

        witnesses.Add(refused reserved)

        let! unchanged =
            person 8610 fixture.Owner "people.changeSignInIdentity" (identity fixture.Owner)

        witnesses.Add(refused unchanged)

        let! invalidEmail =
            person
                8611
                fixture.Owner
                "people.setProfile"
                (Map.ofList
                    [
                        input InputTarget.ProfileDisplayName "Synthetic owner"
                        input InputTarget.ProfileDigestEmail "invalid"
                    ])

        witnesses.Add(refused invalidEmail)

        let target = CaseSelector.Reference "DIAG-AUTHORED"
        let plan = Submissions.reminderPlan "Synthetic follow-up" "2026-11-18"

        let reminder number taskName selected values =
            Service.executeSubmission
                actor
                (Submissions.createRaw
                    taskName
                    (id fixture number)
                    (OperationBinding.Reminder(target, selected))
                    values)
                ct

        let! invalidKind =
            reminder
                8613
                "reminder.create"
                None
                (Map.add
                    (Submissions.input InputTarget.ReminderPlanKind)
                    (SubmissionValue.Text "UNKNOWN")
                    plan)

        witnesses.Add(refused invalidKind)
        let! pending = reminder 8614 "reminder.create" None plan
        accepted pending |> ignore

        for number, date in [ 8615, "invalid"; 8616, "9999-12-31" ] do
            let! rejected =
                reminder
                    number
                    "reminder.complete"
                    (Some(id fixture 8614, 1L))
                    (Map.ofList [ input InputTarget.ReminderDoneOn date ])

            witnesses.Add(refused rejected)

        return List.ofSeq witnesses
    }
