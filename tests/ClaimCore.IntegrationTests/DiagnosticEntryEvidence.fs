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
                    Inputs.paymentFrom
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
            [
                payment "0" "EUR"
                payment "bad" "EUR"
                payment "1" "bad"
                payment "1" "USD"
                payment "751" "EUR"
                remove Guid.Empty
            ] do
            let! reply = case 2L taskName values
            results.Add(refused reply)

        let! paid = case 2L "case.recordPayment" (snd (payment "100" "EUR"))
        accepted paid |> ignore

        for taskName, values in
            [
                remove (id fixture 7999)
                ("case.withdrawDecision", Map.empty)
                "case.decide",
                Submissions.fields (
                    DecisionInput.values (
                        Inputs.decisionWith [ BusinessField.PayableCurrency, "USD" ] decision
                    )
                )
                "case.decide",
                Submissions.fields (
                    DecisionInput.values (
                        Inputs.decisionWith [ BusinessField.PayableAmount, "99" ] decision
                    )
                )
            ] do
            let! reply = case 3L taskName values
            results.Add(refused reply)

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
                (Amount.value fields.Finance.PaidAmount)
                51M
                "Removing 100 from 150 then adding 1 yields fifty-one euros"
        | _ -> failtest "The authorized payment witness requires a disclosed result"

        return List.ofSeq results
    }

// The safe-integer boundary belongs to pure Domain admission. A real accepted history cannot
// be populated to that count in a test; no corrupted PostgreSQL projection is used as evidence.
let technicalCountEvidence () =
    let valid value =
        Result.defaultWith (fun error -> failtestf "Technical boundary fixture: %A" error) value

    let date = CaseFixtures.date "2026-08-20"

    let decision =
        Inputs.decisionWith [ BusinessField.PayableAmount, "999999999999999999" ] decision
        |> Inputs.admittedDecision

    let finance =
        FinancialPosition.create
            (Some decision)
            (Amount.parse "1000000000000" |> valid)
            SafeInteger.Maximum
            (PaymentDateBounds.create date date)
        |> valid

    let payment =
        Inputs.paymentFrom
            [
                BusinessField.PaymentDate, "2026-08-20"
                BusinessField.PaidAmount, "0.0001"
                BusinessField.PaidCurrency, "EUR"
            ]
        |> Inputs.admittedPayment

    Expect.equal
        (FinancialPosition.record (DateOnly(2026, 9, 7)) payment finance)
        (Error FinancialPositionIssue.CountOutOfRange)
        "The financial owner refuses safe-integer saturation without constructing an impossible-sized family"
    // This boundary cannot be reached in a finite PostgreSQL fixture. Verify its public
    // diagnostic independently, without claiming that a forged family reached task admission.
    diagnostic (
        Refusal.invalidInput (InputTarget.Group RecordGroup.Payment, Violation.PaymentCountLimit)
    )

[<Tests>]
let technicalCount =
    testCase
        "financial count saturation and its public diagnostic are independently checked"
        (fun () ->
            Expect.equal
                (technicalCountEvidence ())
                [ ("INVALID_INPUT", None); ("PAYMENT_COUNT_LIMIT", Some "payment") ]
                "The core integer boundary crosses the public diagnostic codec")
