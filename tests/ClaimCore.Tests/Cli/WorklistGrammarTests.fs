module ClaimCore.Tests.WorklistGrammarTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Cli
open ClaimCore.Json

let private prepared arguments =
    Grammar.prepare (fun () -> "10000000-0000-4000-8000-000000000001") (arguments @ [ "--dry-run" ])
    |> Fixtures.accepted
    |> fun prepared -> Frames.admit prepared.Bytes |> Fixtures.accepted

[<Tests>]
let tests =
    testList
        "catalog worklist flags"
        [
            testCase "catalog array bindings require a finite declared vocabulary" (fun () ->
                let schema item =
                    Schema.Object([ "facet", Schema.Array(item, 0, 2) ], [ "facet" ])

                let finite =
                    Schema.Vocabulary(
                        "independentFacet",
                        Schema.OneOf([ Schema.Const "FIRST"; Schema.Const "SECOND" ], None)
                    )

                match Bindings.walk Map.empty Map.empty (schema finite) with
                | [ BindingNode.Input input ] ->
                    Expect.equal
                        input.Kind
                        (BindingKind.Choices("independentFacet", [ "FIRST"; "SECOND" ]))
                        "Finite array choices retain their declared owner"

                    Expect.isFalse input.Required "An omitted facet remains optional"
                | _ -> failtest "A finite array must produce one catalog input"

                Expect.throws
                    (fun () ->
                        Bindings.walk Map.empty Map.empty (schema Codec.text.Schema) |> ignore)
                    "Unbounded text arrays do not silently become finite choices")
            testCase "repeatable facets and recent order cross the real case-list codec" (fun () ->
                let frame =
                    prepared
                        [
                            "case"
                            "list"
                            "--lifecycle"
                            "active"
                            "--lifecycle"
                            "VOIDED"
                            "--status"
                            "opened"
                            "--progress"
                            "undecided"
                            "--progress"
                            "DECIDED"
                            "--text"
                            "Rīga%_\\"
                            "--order"
                            "recent"
                        ]

                let request = Api.caseList.Input.Read "$" (Frames.input frame) |> Fixtures.accepted

                Expect.equal
                    request.Filter.Lifecycles
                    [ Lifecycle.Active; Lifecycle.Voided ]
                    "CLI aliases become exact wire vocabulary"

                Expect.equal request.Filter.Statuses [ CaseStatus.Opened ] "Status facet"

                Expect.equal
                    request.Filter.Progress
                    [ CaseProgress.Undecided; CaseProgress.Decided ]
                    "Progress facet"

                Expect.equal
                    request.Filter.Text
                    (Some "Rīga%_\\")
                    "Authored search is retained for Domain admission"

                Expect.equal request.Filter.Order CaseListOrder.Recent "Recent order")
            testCase
                "repeated selections form a set and scalar flags still reject repeats"
                (fun () ->
                    let frame =
                        prepared
                            [
                                "case"
                                "list"
                                "--lifecycle"
                                "active"
                                "--lifecycle"
                                "ACTIVE"
                                "--lifecycle"
                                "active"
                                "--lifecycle"
                                "ACTIVE"
                            ]

                    let request =
                        Api.caseList.Input.Read "$" (Frames.input frame) |> Fixtures.accepted

                    Expect.equal
                        request.Filter.Lifecycles
                        [ Lifecycle.Active ]
                        "Repeating one set member does not exceed the finite array bound"

                    match
                        Grammar.prepare
                            (fun () -> "unused")
                            [ "case"; "list"; "--limit"; "1"; "--limit"; "2" ]
                    with
                    | Error issues ->
                        Expect.contains
                            issues
                            (UsageError.DuplicateOption "--limit")
                            "Scalar options remain single-valued"
                    | Ok _ -> failtest "A repeated scalar flag cannot dispatch")
            testCase "an assigned option preserves text beginning with a flag prefix" (fun () ->
                let frame =
                    prepared
                        [
                            "case"
                            "list"
                            "--text=--literal"
                            "--progress=decided"
                            "--progress=paid"
                        ]

                let request = Api.caseList.Input.Read "$" (Frames.input frame) |> Fixtures.accepted

                Expect.equal
                    request.Filter.Text
                    (Some "--literal")
                    "The option value is authored text"

                Expect.equal
                    request.Filter.Progress
                    [ CaseProgress.Decided; CaseProgress.Paid ]
                    "Assigned repeatable flags use the same finite choices"

                Expect.isError
                    (Grammar.prepare (fun () -> "unused") [ "case"; "list"; "--dry-run=true" ])
                    "Switches retain their valueless spelling")
            testCase "unselected filters retain the original minimal request" (fun () ->
                let frame = prepared [ "case"; "list" ]

                Expect.equal
                    (Frames.input frame)
                    (JsonValue.Object [ "limit", JsonValue.Integer 50L ])
                    "Default order and empty filters stay implicit")
            testCase "an unknown facet member is rejected by the finite catalog choices" (fun () ->
                match
                    Grammar.prepare
                        (fun () -> "unused")
                        [ "case"; "list"; "--progress"; "unadvertised" ]
                with
                | Error [ UsageError.InvalidChoice("--progress", "unadvertised", choices) ] ->
                    Expect.contains choices "undecided" "Actual finite choices are presented"
                    Expect.contains choices "paid" "Payment choice remains available"
                | _ -> failtest "An unknown progress cannot become a wire request")
        ]
