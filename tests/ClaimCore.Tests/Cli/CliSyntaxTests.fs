module ClaimCore.Tests.CliSyntaxTests

open Expecto
open ClaimCore.Contract
open ClaimCore.Contract.Protocol
open ClaimCore.Cli

let private operationId = "10000000-0000-4000-8000-000000000001"

let private registration =
    [
        "--incident-date"
        "2026-08-01"
        "--incident-notification-date"
        "2026-08-03"
        "--incident-country"
        "LV"
        "--claimant-name"
        "Synthetic claimant"
        "--insurer-name"
        "Synthetic insurer"
        "--claimed-amount"
        "1.0000"
        "--claimed-currency"
        "EUR"
    ]

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
                              Act = CaseAct.Correct {
                                                        Registration = RegistrationGroup.Replace value
                                                    }
                          } ->
        Expect.equal value.ClaimedAmount "1.0000" "Authored amount spelling is preserved"

        Expect.equal
            value.ClaimantName
            "Synthetic claimant"
            "Replacement fields follow their selected branch"
    | _ -> failtest "The requested replacement must remain in the prepared frame"

let private invalidAliasConstants () =
    Expect.isError
        (prepare
            [
                "authority"
                "enable"
                "--kind"
                "human"
                "--issuer"
                "https://issuer.example/"
                "--subject"
                "synthetic"
                "--enabled"
                "true"
            ])
        "Retired boolean inputs cannot be sent"

[<Tests>]
let tests =
    testList
        "CLI owned syntax"
        [
            testCase "correction selectors precede required replacement operands" replacement
            testCase "retired boolean operands are refused" invalidAliasConstants
            testCase "a dotted unknown verb cannot alias a known verb" (fun () ->
                Expect.isError
                    (prepare ([ "case"; "open.extra"; "SYNTHETIC"; "--dry-run" ] @ registration))
                    "Only declared verbs are admitted")
        ]
