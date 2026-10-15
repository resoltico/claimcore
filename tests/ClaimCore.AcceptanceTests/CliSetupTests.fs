module ClaimCore.AcceptanceTests.CliSetupTests

open System.Text
open Expecto
open ClaimCore.AcceptanceTests.CliCalls

[<Tests>]
let tests =
    testCase
        "published authority setup refuses incomplete or unconfirmed session replies"
        (fun () ->
            let accepted = "{\"outcome\":{\"tag\":\"ACCEPTED\",\"data\":{}}}\n"
            let refused = "{\"outcome\":{\"tag\":\"REJECTED\",\"data\":{}}}\n"

            let result exitCode (output: string) (errors: string) : ProcessRunner.Result =
                {
                    ExitCode = exitCode
                    StandardOutput = Encoding.UTF8.GetBytes(output)
                    StandardError = Encoding.UTF8.GetBytes(errors)
                }

            requireAcceptedReplies 2 (result 0 (accepted + accepted) "")

            for incomplete in
                [
                    result 0 "" ""
                    result 0 accepted ""
                    result 0 (accepted + accepted + accepted) ""
                    result 0 (accepted + "\n" + accepted) ""
                    result 0 (accepted.TrimEnd('\n') + accepted) ""
                    result 0 (refused + accepted) ""
                    result 3 (accepted + accepted) ""
                    result 0 (accepted + accepted) "synthetic diagnostic"
                ] do
                Expect.throws
                    (fun () -> requireAcceptedReplies 2 incomplete)
                    "An incomplete or unconfirmed setup cannot qualify")
