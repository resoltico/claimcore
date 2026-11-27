module ClaimCore.Tests.Tasks.CaseChangeReferenceTests

open ClaimCore.Domain
open ClaimCore.Domain.Tasks
open Expecto

[<Tests>]
let retired =
    testCase "retired reference tools and authored reference shapes remain unavailable" (fun () ->
        Expect.isNone
            (Registry.find (TaskId.ofLiteral "case.changeReference"))
            "The retired operation has no declaration"

        Expect.isError
            (CaseReference.parse "AUTHORED-REFERENCE")
            "Only issued reference shapes can be selected")
