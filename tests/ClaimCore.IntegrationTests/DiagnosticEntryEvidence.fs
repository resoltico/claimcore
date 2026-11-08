module ClaimCore.IntegrationTests.DiagnosticEntryEvidence

open System
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

        return List.ofSeq results
    }
