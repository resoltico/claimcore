module ClaimCore.Tests.CliCatalogTests

open ClaimCore.CommandLine

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Cli
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts

let private commandExamples () =
    let path =
        Path.Combine(
            ClaimCore.TestSupport.RepositoryRoot.find (),
            "tests/fixtures/cli-commands.txt"
        )

    let rows = File.ReadAllLines path

    Expect.isNonEmpty rows "CLI examples are not empty"

    for row in rows do
        let arguments = CliExamples.fromCapture row |> CliExamples.complete

        let actual =
            Grammar.prepare
                (fun () -> "11111111-2222-7333-8444-555555555555")
                (arguments @ [ "--dry-run" ])
            |> CaseBuilders.accepted

        Expect.isOk (Frames.admit actual.Bytes) "Prepared request passes the actual endpoint codec"


let private byteLimits () =
    let operationId = "11111111-2222-7333-8444-555555555555"

    let prepare reason =
        let args =
            [
                "case"
                "void"
                "REF"
                "--operation-id"
                operationId
                "--expected-revision"
                "1"
                "--reason"
                reason
                "--dry-run"
            ]

        Grammar.prepare (fun () -> operationId) args

    let emptyInput =
        """{"operationId":"11111111-2222-7333-8444-555555555555","task":"case.void","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"REF"},"expectedRevision":1},"inputs":{"input:reason":""}}"""

    let remaining =
        Limits.MaximumRequestBytes - System.Text.Encoding.UTF8.GetByteCount emptyInput

    let exact = prepare (String('x', remaining)) |> CaseBuilders.accepted
    Expect.isOk (Frames.admit exact.Bytes) "A request at the byte limit remains preparable"

    for reason in [ String('x', remaining + 1); String.replicate 20_000 "😀" ] do
        match prepare reason with
        | Error [ UsageError.RequestTooLarge maximum ] ->
            Expect.equal maximum Limits.MaximumRequestBytes "The frame reader's bound is reported"

            for language in LocalizationPolicy.languages do
                let text =
                    UsageText.render
                        language
                        [ "case"; "void" ]
                        [ UsageError.RequestTooLarge maximum ]

                Expect.stringContains text (string maximum) "Localized limit guidance"
        | Error issues -> failtestf "Wrong size refusal: %A" issues
        | Ok _ -> failtest "Dry-run must not emit a frame the frame reader rejects"

let private operationId = "10000000-0000-7000-8000-000000000001"

let private registration =
    ClaimCore.TestSupport.DomainInputs.registrationWith
        [
            BusinessField.PolicyNumber, "Synthetic claimant"
            BusinessField.InsurerClaimReference, "Synthetic insurer"
            BusinessField.ClaimedAmount, "1.0000"
        ]
        ClaimCore.TestSupport.DomainInputs.registration
    |> CliExamples.registrationArguments

let private prepare args =
    Grammar.prepare (fun () -> operationId) args

let private replacement () =
    let prepared =
        prepare (
            [
                "case"
                "correct"
                "CC-DZXV-W4P3-TR4W-QN99-83"
                "--operation-id"
                operationId
                "--expected-revision"
                "1"
                "--registration"
                "UPDATE"
                "--decision"
                "KEEP"
                "--dry-run"
            ]
            @ registration
        )
        |> Result.defaultWith (fun issues -> failtestf "Correction argv must prepare: %A" issues)

    let frame = Frames.admit prepared.Bytes |> CaseBuilders.accepted

    let request =
        OperationSubmissionCodecs.submission.Read "$" (Frames.input frame)
        |> CaseBuilders.accepted
        |> ClaimCore.TestSupport.Submissions.command

    Expect.equal
        request.Inputs.Values[TaskInputId.Target(InputTarget.Field BusinessField.ClaimedAmount)]
        (InputValue.Text "1.0000")
        "Authored amount spelling is preserved"

    Expect.equal
        request.Inputs.Values[TaskInputId.Target(InputTarget.Field BusinessField.PolicyNumber)]
        (InputValue.Text "Synthetic claimant")
        "Selected replacement fields"


[<Tests>]
let tests =
    testList
        "CLI catalog behavior"
        [
            testCase "typed CLI admits every authored command example" commandExamples
            testCase
                "opening accepts erasable identifiers and refuses an authored permanent reference"
                (fun () ->
                    let prepare operands =
                        Grammar.prepareOffline ([ "case"; "open"; "--dry-run" ] @ operands)

                    let prepared =
                        prepare [ "--policy-number"; "SYNTHETIC-EXTERNAL" ]
                        |> CaseBuilders.accepted
                        |> Option.get

                    use frame = System.Text.Json.JsonDocument.Parse prepared.Bytes
                    let input = frame.RootElement.GetProperty "input"
                    let values = input.GetProperty "inputs"

                    Expect.equal
                        (values.GetProperty("field:policyNumber").GetString())
                        "SYNTHETIC-EXTERNAL"
                        "Preserve the business identifier"

                    Expect.isFalse
                        (fst (values.TryGetProperty "field:caseReference"))
                        "The server allocates the reference"

                    Expect.isFalse
                        (fst (input.TryGetProperty "operationId"))
                        "Dry-run allocates no operation ID"

                    Expect.isError
                        (prepare [ "--case-reference"; "CC-0000-0000-0000-0000-98" ])
                        "The retired flag is refused"

                    Expect.isError
                        (prepare [ "CC-0000-0000-0000-0000-98" ])
                        "The retired operand is refused")
            testCase "case parties is available as a paged CLI query" (fun () ->
                let prepared =
                    prepare
                        [
                            "case"
                            "parties"
                            "CC-DZXV-W4P3-TR4W-QN99-83"
                            "--limit"
                            "2"
                            "--dry-run"
                        ]
                    |> CaseBuilders.accepted

                let frame = Frames.admit prepared.Bytes |> CaseBuilders.accepted

                Expect.equal
                    (Frames.value Api.caseParties frame)
                    (Some
                        {
                            Target = CaseSelector.Reference "CC-DZXV-W4P3-TR4W-QN99-83"
                            Limit = 2
                            AfterCursor = None
                        })
                    "The CLI uses the case party lookup contract")
            testCase "case assignees is available as a paged CLI query" (fun () ->
                let prepared =
                    prepare
                        [
                            "case"
                            "assignees"
                            "CC-DZXV-W4P3-TR4W-QN99-83"
                            "--limit"
                            "2"
                            "--dry-run"
                        ]
                    |> CaseBuilders.accepted

                let frame = Frames.admit prepared.Bytes |> CaseBuilders.accepted

                Expect.equal
                    (Frames.value Api.caseAssignees frame)
                    (Some
                        {
                            Target = CaseSelector.Reference "CC-DZXV-W4P3-TR4W-QN99-83"
                            Limit = 2
                            AfterCursor = None
                        })
                    "The CLI uses the case assignee lookup contract")
            testCase "prepared frames enforce exact and multibyte request size bounds" byteLimits
            testCase "correction replacement retains authored values" replacement
            testCase "a dotted unknown verb cannot alias a known verb" (fun () ->
                Expect.isError
                    (prepare (
                        [ "case"; "open.extra"; "CC-DZXV-W4P3-TR4W-QN99-83"; "--dry-run" ]
                        @ registration
                    ))
                    "Only declared verbs are admitted")
            testCase "lookup reports its unsupported preview command" (fun () ->
                let arguments =
                    [ "operation"; "get"; "11111111-2222-7333-8444-555555555555"; "--preview" ]

                match Grammar.prepare (fun () -> "unused") arguments with
                | Error errors ->
                    Expect.equal
                        errors
                        [ UsageError.NotApplicable("--preview", "operation get") ]
                        "Lookup remains a read without a preview mode"
                | Ok _ -> failtest "Lookup cannot be previewed")
        ]
