module ClaimCore.Tests.CliCatalogTests

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Cli
open ClaimCore.Protocol
open ClaimCore.Domain

let private oracle () =
    let path =
        Path.Combine(ClaimCore.TestSupport.RepositoryRoot.find (), "tests/fixtures/cli-frames.tsv")

    let rows = File.ReadAllLines path

    Expect.equal
        rows.Length
        ClaimCore.TestSupport.Inventory.cliFrameCount
        "All captured invocations remain exercised"

    for row in rows do
        let cells = row.Split('\t', 2)

        let arguments =
            cells[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray
            |> CliExamples.complete

        let actual =
            Grammar.prepare
                (fun () -> "11111111-2222-4333-8444-555555555555")
                (arguments @ [ "--dry-run" ])
            |> Fixtures.accepted

        Expect.isOk (Frames.admit actual.Bytes) "Prepared request passes the actual endpoint codec"

        Expect.equal
            (Encoding.UTF8.GetString actual.Bytes)
            cells[1]
            "The independently authored frame bytes are exact"

let private byteLimits () =
    let operationId = "11111111-2222-4333-8444-555555555555"

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
        """{"kind":"CASE","operationId":"11111111-2222-4333-8444-555555555555","caseReference":"REF","expectedRevision":1,"act":{"kind":"VOID","reason":""}}"""

    let remaining =
        Limits.MaximumRequestBytes - System.Text.Encoding.UTF8.GetByteCount emptyInput

    let exact = prepare (String('x', remaining)) |> Fixtures.accepted
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

let private operationId = "10000000-0000-4000-8000-000000000001"

let private registration =
    ClaimCore.TestSupport.Inputs.registrationWith
        [
            BusinessField.ClaimantName, "Synthetic claimant"
            BusinessField.InsurerName, "Synthetic insurer"
            BusinessField.ClaimedAmount, "1.0000"
        ]
        ClaimCore.TestSupport.Inputs.registration
    |> CliExamples.registrationArguments

let private prepare args =
    Grammar.prepare (fun () -> operationId) args

let private replacement () =
    let prepared =
        prepare (
            [
                "case"
                "correct"
                "SYNTHETIC"
                "--expected-revision"
                "1"
                "--registration"
                "REPLACE"
                "--decision"
                "KEEP"
                "--payment"
                "KEEP"
                "--dry-run"
            ]
            @ registration
        )
        |> Fixtures.accepted

    let frame = Frames.admit prepared.Bytes |> Fixtures.accepted

    let request =
        RequestCodecs.operationInput.Read "$" (Frames.input frame) |> Fixtures.accepted

    match request with
    | OperationInput.Case {
                              Act = AuthoredCaseAct.Correct {
                                                                Registration = Amendment.Replace value
                                                            }
                          } ->
        Expect.equal
            (RegistrationInput.value BusinessField.ClaimedAmount (value))
            "1.0000"
            "Authored amount spelling is preserved"

        Expect.equal
            (RegistrationInput.value BusinessField.ClaimantName (value))
            "Synthetic claimant"
            "Replacement fields follow their selected branch"
    | _ -> failtest "The requested replacement must remain in the prepared frame"

[<Tests>]
let tests =
    testList
        "CLI catalog behavior"
        [
            testCase "typed CLI produces all thirty independently captured frames" oracle
            testCase "prepared frames enforce exact and multibyte request size bounds" byteLimits
            testCase "correction replacement retains authored values" replacement
            testCase "a dotted unknown verb cannot alias a known verb" (fun () ->
                Expect.isError
                    (prepare ([ "case"; "open.extra"; "SYNTHETIC"; "--dry-run" ] @ registration))
                    "Only declared verbs are admitted")
            testCase "operational writes report their actual unsupported preview command" (fun () ->
                let arguments =
                    [ "operation"; "resolve"; "11111111-2222-4333-8444-555555555555"; "--preview" ]

                Expect.equal
                    (Grammar.prepare (fun () -> "unused") arguments)
                    (Error [ UsageError.NotApplicable("--preview", "operation resolve") ])
                    "Resolve is an operational write, so read-command wording is inaccurate")
        ]
