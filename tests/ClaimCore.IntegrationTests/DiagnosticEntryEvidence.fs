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

        // Remain partly paid so the witness isolates collection capacity from full payment.
        for revision in 3L .. 51L do
            let! reply = case revision (payment "1" "EUR")
            accepted reply |> ignore

        let! overCapacity = case 52L (payment "1" "EUR")
        results.Add(refused overCapacity)

        let! removed =
            case 52L (AuthoredCaseAct.RemovePayment(OperationId.value (accepted paid).OperationId))

        let afterRemoval = accepted removed |> caseReceipt

        Expect.equal
            (Revision.value afterRemoval.Revision)
            53L
            "The refusal appended no accepted case revision"

        let! recordedAgain = case 53L (payment "1" "EUR")
        let restored = accepted recordedAgain |> caseReceipt

        match restored.Snapshot with
        | SnapshotDisclosure.Disclosed live ->
            let fields = LiveCase.toState live |> CaseState.fields |> Option.get
            Expect.equal fields.Payments.Length 50 "Removal frees one stored payment slot"

            Expect.equal
                (fields.Payments |> List.sumBy (PaymentEntry.amount >> Amount.value))
                50M
                "Removing 100 from 149 then adding 1 yields fifty euros"
        | _ -> failtest "The authorized payment witness requires a disclosed result"

        return List.ofSeq results
    }
