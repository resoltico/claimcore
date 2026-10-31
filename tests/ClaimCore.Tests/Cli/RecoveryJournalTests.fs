module ClaimCore.Tests.RecoveryJournalTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Cli

let private record (attempt: string) : RecoveryIdentity =
    {
        ServiceOrigin = "https://service.example"
        InstallationId = Guid.Parse "10000000-0000-4000-8000-000000000001"
        ActorId = Guid.Parse "10000000-0000-4000-8000-000000000002"
        AttemptId = Guid.Parse attempt
        OperationId = Guid.Parse "10000000-0000-4000-8000-000000000003"
        Subject = "CASE"
        Act = "OPEN"
    }

[<Tests>]
let tests =
    testList
        "native recovery journal"
        [
            testCase
                "private attempt files retain identity only and concurrent operation IDs do not overwrite"
                (fun () ->
                    let root = Directory.CreateTempSubdirectory "claimcore-recovery-test-"

                    try
                        let directory = Path.Combine(root.FullName, "journal")
                        let first = record "10000000-0000-4000-8000-000000000004"
                        let second = record "10000000-0000-4000-8000-000000000005"

                        Expect.isOk
                            (RecoveryJournal.save directory first)
                            "First attempt publishes"

                        Expect.isOk
                            (RecoveryJournal.save directory second)
                            "Second process/attempt key publishes independently"

                        Expect.isOk
                            (RecoveryJournal.save directory first)
                            "The same owned attempt is idempotent"

                        Expect.equal
                            (File.GetUnixFileMode directory)
                            (UnixFileMode.UserRead
                             ||| UnixFileMode.UserWrite
                             ||| UnixFileMode.UserExecute)
                            "Private directory"

                        let files = Directory.GetFiles directory
                        Expect.hasLength files 2 "Only committed per-attempt files remain"

                        for file in files do
                            Expect.equal
                                (File.GetUnixFileMode file)
                                (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                                "Private file"

                            use json = JsonDocument.Parse(File.ReadAllBytes file)

                            Expect.equal
                                (json.RootElement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
                                (Set.ofList
                                    [
                                        "format"
                                        "serviceOrigin"
                                        "installationId"
                                        "actorId"
                                        "attemptId"
                                        "operationId"
                                        "subject"
                                        "act"
                                    ])
                                "Exact identity-only grammar"

                        Expect.equal
                            (RecoveryJournal.list directory first.Scope)
                            (Ok [ first; second ])
                            "Restart listing finds both attempts"

                        Expect.isOk
                            (RecoveryJournal.remove directory first)
                            "Clear only the owned attempt"

                        Expect.equal
                            (RecoveryJournal.list directory first.Scope)
                            (Ok [ second ])
                            "The other attempt survives"

                        Expect.equal
                            (RecoveryJournal.list
                                directory
                                { first.Scope with
                                    ActorId = Guid.NewGuid()
                                })
                            (Ok [])
                            "Another actor cannot list these records"

                        Expect.equal
                            (RecoveryJournal.list
                                directory
                                { first.Scope with
                                    InstallationId = Guid.NewGuid()
                                })
                            (Ok [])
                            "Another installation cannot list these records"
                    finally
                        root.Delete true)
            testCase
                "unsafe state paths and failed publication refuse without an exposed record"
                (fun () ->
                    Expect.isError
                        (RecoveryJournal.location (fun name ->
                            if name = "XDG_STATE_HOME" then Some "relative" else None))
                        "Relative state roots refuse"

                    let root = Directory.CreateTempSubdirectory "claimcore-recovery-refusal-"

                    try
                        File.SetUnixFileMode(
                            root.FullName,
                            UnixFileMode.UserRead
                            ||| UnixFileMode.UserWrite
                            ||| UnixFileMode.UserExecute
                            ||| UnixFileMode.OtherRead
                        )

                        Expect.isError
                            (RecoveryJournal.save
                                root.FullName
                                (record "10000000-0000-4000-8000-000000000004"))
                            "Public application directories refuse"

                        Expect.isEmpty
                            (Directory.GetFiles root.FullName)
                            "No partial publication or temporary write remains"
                    finally
                        root.Delete true)
        ]

[<Tests>]
let processTermination =
    testCase "an abruptly terminated publisher leaves a complete recoverable attempt" (fun () ->
        let owned = Directory.CreateTempSubdirectory "claimcore-journal-process-"

        try
            let directory = Path.Combine(owned.FullName, "state")
            let script = Path.Combine(owned.FullName, "publisher.fsx")
            let binary = typeof<RecoveryIdentity>.Assembly.Location
            let testBinary = System.Reflection.Assembly.GetExecutingAssembly().Location
            let quote (text: string) = JsonSerializer.Serialize text

            File.WriteAllText(
                script,
                "#I "
                + quote (Path.GetDirectoryName binary |> nonNull)
                + "\n#I "
                + quote (Path.GetDirectoryName testBinary |> nonNull)
                + "\n#r "
                + quote testBinary
                + "\nClaimCore.Tests.RecoveryJournalProcess.publish fsi.CommandLineArgs[1]\n"
            )

            let start =
                System.Diagnostics.ProcessStartInfo(
                    "dotnet",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                )

            for argument in [ "fsi"; "--nologo"; "--exec"; script; directory ] do
                start.ArgumentList.Add argument

            use child = System.Diagnostics.Process.Start(start) |> nonNull
            let errors = child.StandardError.ReadToEndAsync()

            try
                let line =
                    child.StandardOutput
                        .ReadLineAsync()
                        .WaitAsync(TimeSpan.FromSeconds 30.)
                        .GetAwaiter()
                        .GetResult()

                if line <> "published" then
                    child.WaitForExit(5000) |> ignore
                    failtest ("Publisher setup failed: " + errors.GetAwaiter().GetResult())

                child.Kill true
                Expect.isTrue (child.WaitForExit 5000) "The owned process was terminated"
                let expected = record "10000000-0000-4000-8000-000000000004"

                Expect.equal
                    (RecoveryJournal.list directory expected.Scope)
                    (Ok [ expected ])
                    "The replacement process reads complete identity after abrupt termination"

                Expect.isEmpty
                    (Directory.GetFiles(directory, ".attempt-*"))
                    "Atomic publication leaves no temporary write"
            finally
                if not child.HasExited then
                    child.Kill true

                child.WaitForExit(5000) |> ignore
        finally
            owned.Delete true)
