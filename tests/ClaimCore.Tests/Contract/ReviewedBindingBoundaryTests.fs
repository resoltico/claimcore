module ClaimCore.Tests.ReviewedBindingBoundaryTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<Tests>]
let tests =
    testCase "reviewed binding revisions retain exact safe integer admission" (fun () ->
        let declaration =
            ClaimCore.Domain.Tasks.Registry.findDeclaration (TaskId.ofLiteral "case.erase")
            |> Option.get

        let binding revision =
            OperationBinding.Case(
                CaseSelector.Reference "CC-MPF7-EW0T-0XTQ-PS3F-23",
                revision,
                None,
                Some "synthetic-review"
            )

        for revision in [ -1L; Revision.Maximum + 1L; Int64.MaxValue ] do
            Expect.isError
                (OperationSubmission.admitBinding declaration (binding revision))
                "Out-of-range reviewed revision"

        Expect.isOk
            (OperationSubmission.admitBinding declaration (binding Revision.Maximum))
            "Terminal revision is valid input syntax, though no longer writable")
