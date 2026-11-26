module ClaimCore.AcceptanceTests.PublishedAttachmentTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open CliCalls

let private qualify () =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        setup fixture
        let environment = interactiveEnvironment fixture "synthetic-owner" "valid"
        let reference = "ATTACH-CLI-" + (id ()).Substring(24)
        let add = id ()
        let caseId = id ()
        let withdrawal = id ()

        frame
            "operation.execute"
            (caseSubmission
                caseId
                reference
                0L
                "case.open"
                (ClaimCore.TestSupport.Submissions.fields (
                    RegistrationInput.values ClaimCore.TestSupport.DomainInputs.registration
                )))
        |> fun source -> run fixture fixture.Environment source 0
        |> require "ACCEPTED"
        |> ignore

        let sourcePath = Path.Combine(fixture.Directory, "source evidence.txt")
        let destination = Path.Combine(fixture.Directory, "downloaded evidence.txt")
        let content = Array.init 1_048_577 (fun index -> byte (index % 256))
        File.WriteAllBytes(sourcePath, content)

        let cli arguments expected =
            let result = invoke fixture environment arguments Array.empty expected
            Expect.equal result.ExitCode expected "Published attachment command exit"
            result

        let machine arguments expected outcome delivery =
            let reply = cli (arguments @ [ "--format"; "json" ]) expected |> parse expected

            Expect.equal
                (reply.GetProperty("endpoint").GetString())
                "operation.execute"
                "One execution frame"

            Expect.equal
                (reply.GetProperty("delivery").GetString())
                delivery
                "Execution delivery state"

            require outcome reply

        let addArguments =
            [
                "attachment"
                "add"
                reference
                "--file"
                sourcePath
                "--confirm"
                "yes"
                "--operation-id"
                add
            ]

        let uploaded = machine addArguments 0 "ACCEPTED" "DELIVERED"

        Expect.stringContains
            (uploaded.GetRawText())
            "source_evidence.txt"
            "Reviewed portable basename"

        Expect.isFalse (uploaded.GetRawText().Contains sourcePath) "Local path stays private"
        machine addArguments 0 "ALREADY_ACCEPTED" "DELIVERED" |> ignore

        let duplicateArguments =
            (addArguments |> List.take (addArguments.Length - 1)) @ [ id () ]

        let duplicate = machine duplicateArguments 2 "REJECTED" "NOT_SENT"

        Expect.stringContains
            (duplicate.GetRawText())
            "ATTACHMENT_DUPLICATE"
            "Review refuses before upload"

        let blockedPath = Path.Combine(fixture.Directory, "blocked evidence.exe")
        let blockedId = id ()
        File.WriteAllText(blockedPath, "Synthetic attachment for testing filename policy.")

        let blocked =
            machine
                [
                    "attachment"
                    "add"
                    reference
                    "--file"
                    blockedPath
                    "--confirm"
                    "yes"
                    "--operation-id"
                    blockedId
                ]
                2
                "REJECTED"
                "NOT_SENT"

        Expect.stringContains
            (blocked.GetRawText())
            "ATTACHMENT_TYPE_REFUSED"
            "CLI preflight returns the typed name violation"

        let current =
            verb fixture environment [ "case"; "attachments"; reference ] 0
            |> require "SUCCEEDED"
            |> fun data -> data.GetProperty "items"

        Expect.equal (current.GetArrayLength()) 1 "Current list has one accepted attachment"

        Expect.equal
            (current[0].GetProperty("attachmentId").GetString())
            add
            "ID follows the accepting operation"

        Expect.equal
            (current[0].GetProperty("name").GetString())
            "source_evidence.txt"
            "Portable filename"

        let details = cli [ "case"; "attachments"; reference; "--details" ] 0

        Expect.stringContains
            (Encoding.UTF8.GetString details.StandardOutput)
            add
            "The list supplies a downloadable ID"

        let download = [ "attachment"; "download"; reference; add; "--output"; destination ]
        cli download 0 |> ignore

        Expect.sequenceEqual
            (File.ReadAllBytes destination)
            content
            "Exact complete published download"

        Expect.equal
            (File.GetUnixFileMode destination)
            (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            "Complete file is published privately"

        cli download 3 |> ignore

        Expect.sequenceEqual
            (File.ReadAllBytes destination)
            content
            "Existing destination survives a second download"

        let withdrawalArguments =
            [
                "attachment"
                "withdraw"
                "--case-id"
                caseId
                add
                "--confirm"
                "yes"
                "--operation-id"
                withdrawal
            ]

        machine withdrawalArguments 0 "ACCEPTED" "DELIVERED" |> ignore
        machine withdrawalArguments 0 "ALREADY_ACCEPTED" "DELIVERED" |> ignore

        let refusedId = id ()

        machine
            [
                "attachment"
                "withdraw"
                reference
                add
                "--confirm"
                "yes"
                "--operation-id"
                refusedId
            ]
            2
            "REJECTED"
            "NOT_SENT"
        |> ignore

        let shell =
            PublishedProcesses.start
                "python3"
                [
                    Path.Combine(
                        fixture.Root,
                        "tests/ClaimCore.AcceptanceTests/AttachmentShellProbe.py"
                    )
                    fixture.Cli
                    reference
                    caseId
                    add
                    withdrawal
                    id ()
                    blockedPath
                    id ()
                ]
                environment

        let shellResult = PublishedProcesses.run 45000 shell None

        Expect.equal
            shellResult.ExitCode
            0
            "Published terminal shell adjudicates withdrawal retries and refused attachment types"

        let removed =
            verb fixture environment [ "case"; "attachments"; reference ] 0
            |> require "SUCCEEDED"

        Expect.equal
            (removed.GetProperty("items").GetArrayLength())
            0
            "Withdrawal closes the current list"

        let after = Path.Combine(fixture.Directory, "unavailable.txt")
        cli [ "attachment"; "download"; reference; add; "--output"; after ] 2 |> ignore
        Expect.isFalse (File.Exists after) "Refusal cannot create a destination"

        let original = lookupReceipt fixture add

        Expect.isFalse
            (original.GetRawText().Contains "source_evidence.txt")
            "Fresh receipt masks withdrawn filename"

        do! fixture.StopAndCaptureWeb(TimeSpan.FromSeconds 30.)

        let requestsFor (operationId: string) =
            File.ReadLines(Path.Combine(fixture.Directory, "web.stderr"))
            |> Seq.filter (fun line -> line.Contains(operationId, StringComparison.Ordinal))
            |> Seq.choose (fun line ->
                use document = JsonDocument.Parse line
                let root = document.RootElement

                match root.TryGetProperty "operationId", root.TryGetProperty "endpoint" with
                | (true, operation), (true, endpoint) when operation.GetString() = operationId ->
                    endpoint.GetString() |> Option.ofObj
                | _ -> None)
            |> Seq.toList

        Expect.equal
            (requestsFor refusedId)
            [ "operation.preview" ]
            "The server observed no execute for preflight-only refusal"

        Expect.isTrue
            (requestsFor blockedId |> List.forall ((=) "operation.preview"))
            "The CLI rejected the blocked attachment type before upload"

        fixture.Complete()
    }

[<Tests>]
let tests =
    testCaseAsync
        "published CLI attachment review, shell, exact download and withdrawal"
        (async { do! qualify () |> Async.AwaitTask })
