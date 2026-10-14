module ClaimCore.AcceptanceTests.CliSetupTests

open System.Text
open Expecto
open ClaimCore.AcceptanceTests.CliCalls

[<Tests>]
let tests =
    testCase
        "published authority setup refuses incomplete or unconfirmed session replies"
        (fun () ->
            let applied = "{\"outcome\":{\"tag\":\"APPLIED\",\"data\":{}}}\n"
            let refused = "{\"outcome\":{\"tag\":\"REJECTED\",\"data\":{}}}\n"

            let result exitCode (output: string) (errors: string) : ProcessRunner.Result =
                {
                    ExitCode = exitCode
                    StandardOutput = Encoding.UTF8.GetBytes(output)
                    StandardError = Encoding.UTF8.GetBytes(errors)
                }

            requireAppliedReplies 2 (result 0 (applied + applied) "")

            for incomplete in
                [
                    result 0 "" ""
                    result 0 applied ""
                    result 0 (applied + applied + applied) ""
                    result 0 (applied + "\n" + applied) ""
                    result 0 (applied.TrimEnd('\n') + applied) ""
                    result 0 (refused + applied) ""
                    result 3 (applied + applied) ""
                    result 0 (applied + applied) "synthetic diagnostic"
                ] do
                Expect.throws
                    (fun () -> requireAppliedReplies 2 incomplete)
                    "An incomplete or unconfirmed setup cannot qualify")
