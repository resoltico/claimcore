module ClaimCore.AcceptanceTests.CliSetupTests

open System.Text
open System
open System.IO
open Expecto
open ClaimCore.AcceptanceTests.CliCalls
open ClaimCore.TestSupport
open ClaimCore.Wire

let private failureEvidence () =
    let meta =
        {
            OperationId = None
            AttemptKnowledge = AttemptKnowledge.NotSent
            ResolutionEvidence = ResolutionEvidence.NoEvidence
        }

    let encoded =
        Frames.encode (FrameReply.LocalFailure(meta, None, LocalFailure.Internal))

    let output =
        Array.concat [ encoded; [| 10uy |]; Encoding.UTF8.GetBytes("private-marker\n"); encoded ]

    let failure =
        BoundedProcessFailure(Deadline, output, Encoding.UTF8.GetBytes("private-marker"), true)

    for stage, expected in
        [
            "callback-observed\n", "callback-observed"
            "driver-refused-after-password-filled", "driver-refused-after-password-filled"
            "driver-refused-after-private-marker", "unknown"
            "callback-observed\nprivate-marker", "unknown"
            "unavailable", "unavailable"
            "not-applicable", "not-applicable"
        ] do
        let evidence = CliInvocationEvidence.describe stage failure
        Expect.stringContains evidence ("authStage=" + expected + ";") "Closed stage"
        Expect.stringContains evidence "validReplyFrames=1;" "Only complete typed frames count"
        Expect.stringContains evidence "kind=Deadline; cleanupFailed=True" "Original failure"
        Expect.isFalse (evidence.Contains "private-marker") "No captured content is disclosed"

let private requireOriginal writer progress (failure: BoundedProcessFailure) =
    let mutable observed: exn option = None

    try
        CliInvocationEvidence.run 0 writer progress (fun () -> raise failure) |> ignore
    with error ->
        observed <- Some error

    Expect.isTrue
        (observed |> Option.exists (fun value -> obj.ReferenceEquals(value, failure)))
        "Evidence must preserve the original process failure"

let private retainedFailure () =
    let directory = FixtureFiles.directory ()
    let path = Path.Combine(directory, "progress")
    let failure = BoundedProcessFailure(Deadline, [||], [||], false)

    try
        for source, expected in
            [ "password-filled\n", "password-filled"; String('x', 129), "unknown" ] do
            File.WriteAllText(path, source)
            use writer = new StringWriter()
            requireOriginal writer (Some path) failure
            Expect.stringContains (writer.ToString()) ("authStage=" + expected) "Safe file evidence"

        File.Delete path
        use writer = new StringWriter()
        requireOriginal writer (Some path) failure
        Expect.stringContains (writer.ToString()) "authStage=unavailable;" "Missing progress"
        writer.GetStringBuilder().Clear() |> ignore
        requireOriginal writer None failure

        Expect.stringContains
            (writer.ToString())
            "authStage=not-applicable;"
            "Automation has no browser"

        writer.Dispose()
        requireOriginal writer (Some path) failure
    finally
        Directory.Delete(directory, true)

let private returnEvidence () =
    let accepted =
        """{"location":"issuer","redirect":false,"requested":false,"response":"none","network":"none","provider":"credentials","failure":"timeout"}"""

    let decoded = CliBrowserReturnEvidence.decode accepted
    Expect.isSome decoded "A closed browser return category is retained"
    Expect.stringContains decoded.Value "provider=credentials" "Known provider category"

    for invalid in
        [
            accepted.Replace("credentials", "private-marker")
            accepted.Replace("false", "\"private-marker\"")
            accepted.Replace("\"failure\":\"timeout\"", "\"private-marker\":\"timeout\"")
            accepted.Replace("{", "{\"private-marker\":true,")
            accepted.Replace("{", "{\"location\":\"private-marker\",")
            accepted.Replace(",\"failure\":\"timeout\"", "")
            accepted + "private-marker"
            "[]"
        ] do
        Expect.isNone
            (CliBrowserReturnEvidence.decode invalid)
            "Unadmitted content is never reported"

let private unexpectedResult () =
    let directory = FixtureFiles.directory ()
    let progress = Path.Combine(directory, "progress")

    let result: ProcessRunner.Result =
        {
            ExitCode = 3
            StandardOutput = Encoding.UTF8.GetBytes("private-marker")
            StandardError = [||]
        }

    try
        File.WriteAllText(progress, "driver-refused-after-login-submitted")

        for bytes in [ [| 255uy |]; Array.create 513 (byte 'x') ] do
            File.WriteAllBytes(progress + ".return", bytes)
            use writer = new StringWriter()
            let returned = CliInvocationEvidence.run 0 writer (Some progress) (fun () -> result)
            Expect.isTrue (obj.ReferenceEquals(returned, result)) "The original result is retained"

            Expect.stringContains
                (writer.ToString())
                "exitCode=3"
                "Unexpected exits retain phase evidence"

            Expect.stringContains
                (writer.ToString())
                "browserReturn=unavailable"
                "Unadmitted bytes stay private"

            Expect.isFalse
                (writer.ToString().Contains "private-marker")
                "No captured data is disclosed"

        use writer = new StringWriter()
        writer.Dispose()
        let returned = CliInvocationEvidence.run 0 writer (Some progress) (fun () -> result)
        Expect.isTrue (obj.ReferenceEquals(returned, result)) "Writer failure preserves the result"
    finally
        Directory.Delete(directory, true)

[<Tests>]
let evidenceTests =
    testList
        "published CLI invocation failure evidence"
        [
            testCase "closed stages and complete frames exclude private content" failureEvidence
            testCase
                "bounded file evidence and writer failures preserve the original failure"
                retainedFailure
            testCase "browser return evidence has exact closed fields" returnEvidence
            testCase
                "unexpected exits retain safe evidence and the original result"
                unexpectedResult
        ]

[<Tests>]
let tests =
    testCase
        "published authority setup refuses incomplete or unconfirmed session replies"
        (fun () ->
            let accepted =
                """{"kind":"result","endpoint":"authority.execute","operationId":"10000000-0000-4000-8000-000000000001","attemptKnowledge":"ACCEPTED","resolutionEvidence":"CONTENT_ACCEPTED","outcome":{"tag":"ACCEPTED","data":{"operationId":"10000000-0000-4000-8000-000000000001"}}}"""
                + "\n"

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
                    result
                        0
                        (accepted.Replace(
                            "\"attemptKnowledge\":\"ACCEPTED\"",
                            "\"attemptKnowledge\":\"UNCONFIRMED\""
                         )
                         + accepted)
                        ""
                    result 0 (accepted.Replace("CONTENT_ACCEPTED", "NONE") + accepted) ""
                ] do
                Expect.throws
                    (fun () -> requireAcceptedReplies 2 incomplete)
                    "An incomplete or unconfirmed setup cannot qualify")
