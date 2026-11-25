module ClaimCore.Tests.AttachmentClientTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading
open Expecto
open ClaimCore.Cli
open ClaimCore.CommandLine
open ClaimCore.Wire
open ClaimCore.Domain

let private expectedName = AttachmentName.parse "Résumé.txt" |> Option.get
let private disposition = "attachment; filename*=UTF-8''R%C3%A9sum%C3%A9.txt"
let private bytes = Encoding.UTF8.GetBytes "abc"
let private digest = "sha-256=:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=:"
let private installation = Guid.Parse "f43a4a58-f27a-4f13-9ed1-b102d46f9ce7"

let private response () =
    let message = new HttpResponseMessage(HttpStatusCode.OK)
    message.Content <- new ByteArrayContent(bytes)
    message.Headers.Add("ClaimCore-Installation", installation.ToString("D"))
    message.Headers.Add(AttachmentTransport.digestHeader, digest)
    message.Headers.CacheControl <- CacheControlHeaderValue(NoStore = true, NoTransform = true)
    message.Content.Headers.ContentLength <- int64 bytes.Length
    message.Content.Headers.ContentEncoding.Add "identity"

    message.Content.Headers.ContentType <-
        MediaTypeHeaderValue AttachmentTransport.downloadMediaType

    message.Content.Headers.TryAddWithoutValidation("Content-Disposition", disposition)
    |> ignore

    message

let private received header change =
    async {
        use value = response ()
        change value

        let! result =
            AttachmentBinaryCall.receive header expectedName value CancellationToken.None
            |> Async.AwaitTask

        return result
    }

[<Tests>]
let tests =
    testList
        "attachment client"
        [
            testCaseAsync
                "binary failures admit only a canonical digest on a typed mismatch"
                (async {
                    let host = String.replicate 64 "a"

                    for status, code, headers, expected in
                        [
                            HttpStatusCode.Conflict, "CONTRACT_MISMATCH", [ host ], Some host
                            HttpStatusCode.Conflict, "CONTRACT_MISMATCH", [], None
                            HttpStatusCode.Conflict, "CONTRACT_MISMATCH", [ host; host ], None
                            HttpStatusCode.Conflict,
                            "CONTRACT_MISMATCH",
                            [ host + "," + host ],
                            None
                            HttpStatusCode.Conflict, "CONTRACT_MISMATCH", [ host + " " ], None
                            HttpStatusCode.Conflict,
                            "CONTRACT_MISMATCH",
                            [ host.ToUpperInvariant() ],
                            None
                            HttpStatusCode.Conflict,
                            "CONTRACT_MISMATCH",
                            [ host.Substring(1) ],
                            None
                            HttpStatusCode.Conflict, "INSTALLATION_MISMATCH", [ host ], None
                            HttpStatusCode.Forbidden, "CONTRACT_MISMATCH", [ host ], None
                            HttpStatusCode.Conflict, "UNKNOWN", [ host ], None
                        ] do
                        let! result =
                            received installation (fun response ->
                                response.StatusCode <- status

                                response.Content <-
                                    new StringContent(
                                        $"""{{"hostFailure":{{"code":"{code}","phase":"NOT_STARTED"}}}}""",
                                        Encoding.UTF8,
                                        "application/json"
                                    )

                                response.Headers.TryAddWithoutValidation(
                                    "ClaimCore-Host-Contract",
                                    (headers: seq<string>)
                                )
                                |> ignore)

                        match result with
                        | Error(AttachmentDownloadFailure.Host(_, supplied)) ->
                            Expect.equal
                                supplied
                                expected
                                "Only an admitted typed mismatch may convey the host build"
                        | Error(AttachmentDownloadFailure.Local ClientFailure.InvalidResponse) ->
                            Expect.isTrue
                                (status <> HttpStatusCode.Conflict || code = "UNKNOWN")
                                "Only malformed failure responses fail transport admission"
                        | _ -> failtest "Expected an attachment response refusal"
                })
            testCase
                "frozen upload reads a regular handle and uses the selected basename"
                (fun () ->
                    if CliIdentity.supportsRemote then
                        let directory =
                            Directory.CreateTempSubdirectory "claimcore-attachment-tests-"

                        try
                            let path = Path.Combine(directory.FullName, "evidence.txt")
                            File.WriteAllBytes(path, bytes)

                            let snapshot =
                                AttachmentFiles.capture path
                                |> Result.defaultWith (fun _ -> failtest "Regular file refused")

                            File.WriteAllText(path, "replacement")

                            Expect.equal
                                snapshot.Name
                                "evidence.txt"
                                "Only the basename is authored"

                            Expect.sequenceEqual
                                snapshot.Bytes
                                bytes
                                "The invocation retains immutable bytes"

                            Expect.equal
                                (AttachmentDigest.value snapshot.Digest)
                                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
                                "Independent digest for abc"

                            use writer = File.OpenWrite path
                            writer.SetLength(int64 AttachmentLimits.FileBytes + 1L)

                            match AttachmentFiles.capture path with
                            | Error(AttachmentCaptureFailure.TooLarge 26_214_401L) -> ()
                            | _ -> failtest "The known regular-file length was not reported."

                            let arguments = [ "attachment"; "add"; "SYNTHETIC"; "--file"; path ]

                            match
                                Grammar.prepare
                                    (fun () -> Guid.CreateVersion7().ToString("D"))
                                    arguments
                            with
                            | Error [ UsageError.AttachmentFileTooLarge(26_214_400, 26_214_401L) as issue ] ->
                                let bytes = ClaimCore.Json.Codec.encode UsageError.codec issue
                                use frame = System.Text.Json.JsonDocument.Parse bytes
                                let summary = frame.RootElement

                                Expect.equal
                                    (summary.GetProperty("kind").GetString())
                                    "ATTACHMENT_FILE_TOO_LARGE"
                                    "The machine error kind"

                                Expect.equal
                                    (summary.GetProperty("maximumBytes").GetInt32())
                                    26_214_400
                                    "The declared byte limit"

                                Expect.equal
                                    (summary.GetProperty("actualBytes").GetString())
                                    "26214401"
                                    "Exact observed bytes"
                            | _ ->
                                failtest "Expected only a typed, local size refusal from the CLI."

                            match AttachmentFiles.capture (path + ".missing") with
                            | Error AttachmentCaptureFailure.Unreadable -> ()
                            | _ -> failtest "The missing source was not reported as unreadable."
                        finally
                            directory.Delete true)
            testCase "opening a FIFO without a writer completes and rejects the handle" (fun () ->
                if CliIdentity.supportsRemote then
                    let directory = Directory.CreateTempSubdirectory "claimcore-attachment-tests-"

                    try
                        let path = Path.Combine(directory.FullName, "fifo")
                        let start = ProcessStartInfo("mkfifo", UseShellExecute = false)
                        start.ArgumentList.Add path
                        use child = Process.Start start |> nonNull
                        Expect.isTrue (child.WaitForExit 5000) "FIFO creation has a deadline"
                        Expect.equal child.ExitCode 0 "FIFO setup succeeds"

                        let capture =
                            System.Threading.Tasks.Task.Run(fun () -> AttachmentFiles.capture path)

                        Expect.isTrue (capture.Wait 2000) "Open is nonblocking"
                        Expect.isError capture.Result "Only regular handles are supported"

                        Expect.isError
                            (AttachmentFiles.capture "/dev/null")
                            "Device contents are unavailable"
                    finally
                        directory.Delete true)
            testCase
                "private final publication refuses existing files and dangling links"
                (fun () ->
                    if CliIdentity.supportsRemote then
                        let directory =
                            Directory.CreateTempSubdirectory "claimcore-attachment-tests-"

                        try
                            let path = Path.Combine(directory.FullName, "download")

                            Expect.isOk
                                (AttachmentFiles.save path bytes CancellationToken.None)
                                "Complete bytes publish"

                            Expect.sequenceEqual
                                (File.ReadAllBytes path)
                                bytes
                                "Published bytes are exact"

                            Expect.equal
                                (File.GetUnixFileMode path)
                                (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                                "Owner-only mode"

                            Expect.isError
                                (AttachmentFiles.save path [| 0uy |] CancellationToken.None)
                                "Existing file refuses"

                            Expect.sequenceEqual
                                (File.ReadAllBytes path)
                                bytes
                                "Existing file survives"

                            let dangling = Path.Combine(directory.FullName, "link")
                            let missing = Path.Combine(directory.FullName, "missing")
                            File.CreateSymbolicLink(dangling, missing) |> ignore

                            Expect.isError
                                (AttachmentFiles.save dangling bytes CancellationToken.None)
                                "Dangling link refuses"

                            Expect.isFalse (File.Exists missing) "No link target was written"

                            Expect.equal
                                (Directory.EnumerateFileSystemEntries(directory.FullName)
                                 |> Seq.length)
                                2
                                "Staging entries were cleaned"
                        finally
                            directory.Delete true)
            testCase "exclusive destination publication has one complete winner" (fun () ->
                if CliIdentity.supportsRemote then
                    let directory = Directory.CreateTempSubdirectory "claimcore-attachment-race-"

                    try
                        let path = Path.Combine(directory.FullName, "contended")
                        use together = new Barrier(2)
                        let candidates = [| Array.create 131_073 9uy; Array.create 131_073 7uy |]

                        let writes =
                            candidates
                            |> Array.map (fun candidate ->
                                System.Threading.Tasks.Task.Run(fun () ->
                                    if
                                        not (together.SignalAndWait(TimeSpan.FromSeconds 5.0))
                                    then
                                        failtest "Both publishers reached the starting barrier"

                                    AttachmentFiles.save path candidate CancellationToken.None))

                        let results =
                            System.Threading.Tasks.Task.WhenAll(writes).GetAwaiter().GetResult()

                        let winner = results |> Array.indexed |> Array.filter (snd >> Result.isOk)
                        Expect.hasLength winner 1 "The hard link never replaces a destination"
                        let index, _ = winner[0]

                        Expect.sequenceEqual
                            (File.ReadAllBytes path)
                            candidates[index]
                            "Only the complete winner is visible"

                        let notWritten = Path.Combine(directory.FullName, "cancelled")
                        let cancelled = CancellationToken(true)

                        Expect.throwsT<OperationCanceledException>
                            (fun () -> AttachmentFiles.save notWritten bytes cancelled |> ignore)
                            "Cancellation before publication"

                        Expect.isFalse (File.Exists notWritten) "Cancelled work has no destination"
                    finally
                        directory.Delete true)
            testCase
                "binary downloads derive the public command and required output help"
                (fun () ->
                    let output =
                        Discovery.commandHelp
                            (RenderingContext.forLanguage "en")
                            [ "attachment"; "download" ]
                        |> Option.defaultWith (fun () ->
                            failtest "Attachment download help must be available.")

                    for text in
                        [
                            "Download"
                            "Save the current attachment to a file."
                            "attachment download"
                            "--output PATH"
                            "ATTACHMENT-ID"
                            "CASE-REFERENCE"
                        ] do
                        Expect.stringContains
                            output
                            text
                            "Help renders the declared transfer with CLI output syntax"

                    Expect.isFalse (output.Contains "[--output PATH]") "Output is required")
            testCaseAsync
                "verified transfer requires the confirmed installation, digest and exact bytes"
                (async {
                    let! success = received installation ignore
                    Expect.isOk success "Exact bytes and headers admit"

                    Expect.sequenceEqual
                        (Result.defaultValue [||] success)
                        bytes
                        "Digest witness is independent"

                    let! wrongOwner = received (Guid.NewGuid()) ignore
                    Expect.isError wrongOwner "Wrong installation refuses"

                    let! wrongDigest =
                        received installation (fun value ->
                            value.Headers.Remove AttachmentTransport.digestHeader |> ignore

                            value.Headers.Add(
                                AttachmentTransport.digestHeader,
                                "sha-256=:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=:"
                            ))

                    Expect.isError wrongDigest "Wrong digest refuses"

                    let! longContent =
                        received installation (fun value ->
                            value.Content.Headers.ContentLength <- 2L)

                    Expect.isError longContent "Overlong body refuses"

                    let withDisposition (disposition: string) (value: HttpResponseMessage) =
                        value.Content.Headers.Remove "Content-Disposition" |> ignore

                        value.Content.Headers.TryAddWithoutValidation(
                            "Content-Disposition",
                            disposition
                        )
                        |> ignore

                    let! otherCanonicalName =
                        received
                            installation
                            (withDisposition "attachment; filename*=UTF-8''different.txt")

                    Expect.isError
                        otherCanonicalName
                        "A canonical header for a different attachment name refuses"

                    let! fallback =
                        received
                            installation
                            (withDisposition
                                "attachment; filename=Rsum.txt; filename*=UTF-8''R%C3%A9sum%C3%A9.txt")

                    Expect.isError fallback "An additional ASCII filename refuses"
                })
            match
                Environment.GetEnvironmentVariable "CLAIMCORE_ATTACHMENT_TEST_NO_HARDLINK_DIRECTORY"
            with
            | null -> ()
            | fixture ->
                testCase "exclusive output works on a real filesystem without hard links" (fun () ->
                    let directory =
                        Directory.CreateDirectory(
                            Path.Combine(fixture, Guid.NewGuid().ToString("N"))
                        )

                    try
                        let source = Path.Combine(directory.FullName, "probe")
                        let link = Path.Combine(directory.FullName, "link")
                        let destination = Path.Combine(directory.FullName, "destination")
                        File.WriteAllBytes(source, bytes)

                        let arguments =
                            ProcessStartInfo(
                                "ln",
                                UseShellExecute = false,
                                RedirectStandardError = true
                            )

                        arguments.ArgumentList.Add source
                        arguments.ArgumentList.Add link
                        use probe = Process.Start arguments |> nonNull

                        Expect.isTrue
                            (probe.WaitForExit 5000)
                            "Independent hard-link probe has a deadline"

                        Expect.notEqual probe.ExitCode 0 "The writable fixture refuses hard links"

                        Expect.isOk
                            (AttachmentFiles.save destination bytes CancellationToken.None)
                            "Verified bytes publish"

                        Expect.sequenceEqual
                            (File.ReadAllBytes destination)
                            bytes
                            "Fallback publishes exact bytes"

                        Expect.isError
                            (AttachmentFiles.save destination [| 0uy |] CancellationToken.None)
                            "Fallback refuses an existing destination"

                        Expect.sequenceEqual
                            (File.ReadAllBytes destination)
                            bytes
                            "Existing contents survive a second save"
                    finally
                        directory.Delete true)
        ]
