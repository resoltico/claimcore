module ClaimCore.Tests.HistoryControlDocumentTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCase "case documents project the authorized history-access control" (fun () ->
        let original = ResponseExamples.caseRead

        for access in [ HistoryAccess.Full; HistoryAccess.Summary; HistoryAccess.None ] do
            let advice =
                CaseAdvice.create
                    original.Advice.Acts
                    original.Advice.Correction
                    access
                    original.Advice.Blocked
                |> Result.defaultWith failtest

            let document = HumanCaseDocument.ofCore { original with Advice = advice }

            Expect.equal document.History (Some access) "Exactly the authorized history hint"

            Expect.isOk
                (Codec.encode HumanDocumentCodecs.document document
                 |> Codec.decode 65536 HumanDocumentCodecs.document)
                "Actual native boundary")
