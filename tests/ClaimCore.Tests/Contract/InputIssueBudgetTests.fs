module ClaimCore.Tests.InputIssueBudgetTests

open Expecto
open ClaimCore.Protocol

[<Tests>]
let tests =
    testCase
        "the diagnostic issue bound equals the widest published operation input count"
        (fun () ->
            let maximum =
                OperationCatalog.mutations
                |> List.map (OperationCatalog.derive >> fst >> List.length)
                |> List.max

            Expect.equal
                DiagnosticCodecs.MaximumInputIssues
                maximum
                "Schema and authored binding limits cannot drift")
