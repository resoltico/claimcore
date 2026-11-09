module ClaimCore.IntegrationTests.DiagnosticEntryEvidence

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
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

        let case revision act =
            Service.execute
                actor
                (OperationInput.Case
                    {
                        OperationId = next ()
                        Target = target
                        ExpectedRevision = revision
                        Act = act
                    })
                ct

        let note act =
            Service.execute
                actor
                (OperationInput.Note
                    {
                        OperationId = next ()
                        Target = target
                        Act = act
                    })
                ct

        let payment amount currency =
            AuthoredCaseAct.RecordPayment(
                Inputs.paymentFrom
                    [
                        BusinessField.PaymentDate, "2026-08-20"
                        BusinessField.PaidAmount, amount
                        BusinessField.PaidCurrency, currency
                    ]
            )

        let! opened = case 0L (AuthoredCaseAct.Open registration)
        accepted opened |> ignore

        for field in [ BusinessField.PolicyNumber; BusinessField.InsurerClaimReference ] do
            let patch =
                RegistrationUpdateInput.create [ field, Some "" ]
                |> Result.defaultWith invalidOp

            let! reply = case 1L (AuthoredCaseAct.AmendRegistration patch)
            results.Add(refused reply)

        for act in [ AuthoredNoteAct.AddNote ""; AuthoredNoteAct.RemoveNote Guid.Empty ] do
            let! reply = note act
            results.Add(refused reply)

        let! decided = case 1L (AuthoredCaseAct.Decide decision)
        accepted decided |> ignore

        for act in
            [
                payment "0" "EUR"
                payment "bad" "EUR"
                payment "1" "bad"
                payment "1" "USD"
                payment "751" "EUR"
                AuthoredCaseAct.RemovePayment Guid.Empty
            ] do
            let! reply = case 2L act
            results.Add(refused reply)

        let! paid = case 2L (payment "100" "EUR")
        accepted paid |> ignore

        for act in
            [
                AuthoredCaseAct.RemovePayment(id fixture 7999)
                AuthoredCaseAct.Bare BareAct.WithdrawDecision
                AuthoredCaseAct.Decide(
                    Inputs.decisionWith [ BusinessField.PayableCurrency, "USD" ] decision
                )
                AuthoredCaseAct.Decide(
                    Inputs.decisionWith [ BusinessField.PayableAmount, "99" ] decision
                )
            ] do
            let! reply = case 3L act
            results.Add(refused reply)

        // Keep an outstanding balance while crossing the retired fifty-payment boundary.
        for revision in 3L .. 51L do
            let! reply = case revision (payment "1" "EUR")
            accepted reply |> ignore

        let! fiftyFirst = case 52L (payment "1" "EUR")
        accepted fiftyFirst |> ignore

        let! removed =
            case 53L (AuthoredCaseAct.RemovePayment(OperationId.value (accepted paid).OperationId))

        let afterRemoval = accepted removed |> caseReceipt

        Expect.equal
            (Revision.value afterRemoval.Revision)
            54L
            "The fifty-first payment and removal each advance revision"

        let! recordedAgain = case 54L (payment "1" "EUR")
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

    let parent =
        {
            CaseSnapshot.CaseId =
                CaseFixtures.caseId (Guid.Parse "70000000-0000-7000-8000-000000000001")
            Reference = CaseFixtures.reference "TECHNICAL-COUNT"
            Revision = Revision.first
            State =
                CaseState.Active
                    {
                        CaseReference = CaseFixtures.reference "TECHNICAL-COUNT"
                        Status = CaseStatus.Opened
                        Registration = Inputs.admittedRegistration registration
                        Finance = finance
                    }
        }

    let today = DateOnly(2026, 9, 7)

    let request =
        {
            OperationId = Guid.Parse "70000000-0000-7000-8000-000000000002"
            CaseReference = "TECHNICAL-COUNT"
            ExpectedRevision = 1L
            Act =
                CaseAct.RecordPayment(
                    Inputs.paymentFrom
                        [
                            BusinessField.PaymentDate, "2026-08-20"
                            BusinessField.PaidAmount, "0.0001"
                            BusinessField.PaidCurrency, "EUR"
                        ]
                )
        }

    let command = Case.admit today (Some parent) request |> valid
    let context = CasePaymentContext.Recording(parent.CaseId, parent.Revision, false)

    match Case.decideSnapshot today context command (Some parent) with
    | Error(CaseDecisionError.Refused(Refusal.InvalidInput issues as refusal)) ->
        Expect.equal
            (InputIssues.toList issues |> List.map _.Violation)
            [ Violation.PaymentCountLimit ]
            "Actual Domain request reaches only the technical count boundary"

        diagnostic refusal
    | other -> failtestf "Expected technical integer boundary refusal: %A" other

[<Tests>]
let technicalCount =
    testCase "technical payment count boundary produces its exact diagnostic" (fun () ->
        Expect.equal
            (technicalCountEvidence ())
            [ ("INVALID_INPUT", None); ("PAYMENT_COUNT_LIMIT", Some "payment") ]
            "The core integer boundary crosses the public diagnostic codec")
