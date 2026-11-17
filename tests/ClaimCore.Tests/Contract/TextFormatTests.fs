module ClaimCore.Tests.TextFormatTests

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.CaseBuilders
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

let private restored (name: string) =
    let encoded =
        ClaimCore.Tests.CaseBuilders.opened ()
        |> CaseFamily.snapshot
        |> Option.get
        |> ClaimCore.RecordFormat.CaseRecord.encodeSnapshot

    let root =
        System.Text.Json.Nodes.JsonNode.Parse(Text.Encoding.UTF8.GetString encoded)
        |> nonNull

    let state = root["state"] |> nonNull
    let fields = state["fields"] |> nonNull
    let registration = fields["registration"] |> nonNull
    registration["claimantName"] <- System.Text.Json.Nodes.JsonValue.Create name
    registration["insurerName"] <- System.Text.Json.Nodes.JsonValue.Create name

    root.ToJsonString()
    |> Text.Encoding.UTF8.GetBytes
    |> ClaimCore.RecordFormat.CaseRecord.decodeSnapshot

let private authored name =
    let values =
        openingInputs.Values
        |> Map.add
            (TaskInputId.Target(InputTarget.Field BusinessField.ClaimantName))
            (InputValue.Text name)
        |> Map.add
            (TaskInputId.Target(InputTarget.Field BusinessField.InsurerName))
            (InputValue.Text name)

    InputMap.create values Map.empty |> Option.get

let private format name =
    match ClaimCore.Domain.Tasks.Case.Open.check (authored name) with
    | [] -> Ok()
    | issues -> Error issues

let private formatBoundaries () =
    for value in [ String('a', 1024); String.replicate 256 "😀"; String('\u0001', 1024) ] do
        Expect.isOk
            (restored value)
            "Accepted history is judged by format, independently of admission"

        Expect.isOk (format value) "Request format uses the same permanent byte bound"

    for value in [ String('a', 1025); String.replicate 257 "😀"; String('\u0001', 1025) ] do
        Expect.isError (restored value) "History cannot exceed the byte grammar"
        Expect.isError (format value) "New requests cannot exceed the byte grammar"

let private admissionInsideFormat () =
    Expect.isLessThanOrEqual
        (4 * PartyName.MaximumCharacters)
        PartyName.MaximumBytes
        "Every admitted scalar sequence fits the permanent byte bound"

    let name = String.replicate 200 "😀"

    let decide value =
        ClaimCore.Domain.Tasks.Case.Open.decide
            (context 1)
            environment
            None
            (Binding.CaseNew Revision.zero)
            (authored value)

    Expect.isOk (decide name) "800 UTF-8 bytes admit"
    Expect.isOk (format (name + "😀")) "Format still admits"
    Expect.isError (decide (name + "😀")) "Admission refuses"

let private stewardshipBoundaries () =
    for check in
        [
            ClaimCore.Domain.Tasks.Case.Void.check
            ClaimCore.Domain.Tasks.Case.Reinstate.check
        ] do
        let proposed value =
            InputMap.create
                (Map.ofList [ TaskInputId.Target InputTarget.Reason, InputValue.Text value ])
                Map.empty
            |> Option.get

        Expect.isEmpty (check (proposed (String('a', 1024)))) "Reason format boundary admits"

        Expect.isFalse
            (check (proposed (String('a', 1025))) |> List.isEmpty)
            "Reason format boundary refuses"

[<Tests>]
let tests =
    testList
        "permanent text byte bounds"
        [
            testCase "names use UTF-8 byte boundaries on requests and restoration" formatBoundaries
            testCase
                "new assertions retain the scalar admission limit inside format"
                admissionInsideFormat
            testCase "both reason-bearing acts enforce the format bound" stewardshipBoundaries
        ]
