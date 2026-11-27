module ClaimCore.Tests.RecoveryJournalTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Application.Contracts

let private record (attempt: string) : RecoveryIdentity =
    {
        ServiceOrigin = "https://service.example"
        InstallationId = Guid.Parse "10000000-0000-7000-8000-000000000001"
        ActorId = Guid.Parse "10000000-0000-7000-8000-000000000002"
        AttemptId = Guid.Parse attempt
        OperationId = Guid.Parse "10000000-0000-7000-8000-000000000003"
        Task = "case.open"
        Refused = false
    }

[<Tests>]
let everyMutation =
    testCase "every human mutation task round-trips through the CLI recovery journal" (fun () ->
        let root = Directory.CreateTempSubdirectory "claimcore-journal-tasks-"

        try
            let kinds =
                TaskDeclarations.all
                |> List.choose (fun declaration -> declaration.Ledger |> Option.map _.Id)

            Expect.isNonEmpty kinds "The actual human task inventory is exercised"

            for kind in kinds do
                let identity =
                    { record (Guid.NewGuid().ToString("D")) with
                        Task = TaskId.token kind
                    }

                Expect.isOk (RecoveryJournal.save root.FullName identity) (TaskId.token kind)

                let read =
                    RecoveryJournal.list root.FullName identity.Scope
                    |> Result.defaultWith (fun () -> failtest "Journal read failed")

                Expect.contains
                    read.Records
                    identity
                    "The declared task identity survives restart decoding"
        finally
            root.Delete true)

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
                        let first = record "10000000-0000-7000-8000-000000000004"
                        let second = record "10000000-0000-7000-8000-000000000005"

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
                                        "task"
                                        "refused"
                                    ])
                                "Exact identity-only grammar"

                        Expect.equal
                            (RecoveryJournal.list directory first.Scope)
                            (Ok
                                {
                                    Records = [ first; second ]
                                    Damaged = 0
                                })
                            "Restart listing finds both attempts"

                        Expect.isOk
                            (RecoveryJournal.remove directory first)
                            "Clear only the owned attempt"

                        Expect.equal
                            (RecoveryJournal.list directory first.Scope)
                            (Ok { Records = [ second ]; Damaged = 0 })
                            "The other attempt survives"

                        Expect.equal
                            (RecoveryJournal.list
                                directory
                                { first.Scope with
                                    ActorId = Guid.NewGuid()
                                })
                            (Ok { Records = []; Damaged = 0 })
                            "Another actor cannot list these records"

                        Expect.equal
                            (RecoveryJournal.list
                                directory
                                { first.Scope with
                                    InstallationId = Guid.NewGuid()
                                })
                            (Ok { Records = []; Damaged = 0 })
                            "Another installation cannot list these records"
                    finally
                        root.Delete true)
            testCase
                "unsafe state paths and failed publication refuse without an exposed record"
                (fun () ->
                    let location state home =
                        RecoveryJournal.location (function
                            | "XDG_STATE_HOME" -> state
                            | "HOME" -> home
                            | _ -> None)

                    Expect.equal
                        (location (Some "relative") (Some "/absolute"))
                        (Error(
                            ClientFailure.RecoveryRootNotAbsolute RecoveryRootSetting.XdgStateHome
                        ))
                        "An explicit relative state root refuses even with an absolute HOME"

                    Expect.equal
                        (location (Some "") (Some "relative"))
                        (Error(ClientFailure.RecoveryRootNotAbsolute RecoveryRootSetting.Home))
                        "The HOME fallback identifies its own setting"

                    Expect.equal
                        (location None (Some "/absolute"))
                        (Ok "/absolute/.local/state/claimcore")
                        "The absent state root uses the absolute HOME fallback"

                    Expect.equal
                        (location (Some "/explicit") (Some "/fallback"))
                        (Ok "/explicit/claimcore")
                        "An absolute explicit root takes precedence"

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
                                (record "10000000-0000-7000-8000-000000000004"))
                            "Public application directories refuse"

                        Expect.isEmpty
                            (Directory.GetFiles root.FullName)
                            "No partial publication or temporary write remains"
                    finally
                        root.Delete true)
        ]

[<Tests>]
let refusalKnowledge =
    testCase "a delivered refusal remains recoverable until final confirmation" (fun () ->
        let root = Directory.CreateTempSubdirectory "claimcore-refused-journal-"

        try
            let identity = record "10000000-0000-7000-8000-000000000004"
            Expect.isOk (RecoveryJournal.save root.FullName identity) "Publish before send"
            Expect.isOk (RecoveryJournal.markRefused root.FullName identity) "Retain known refusal"

            Expect.equal
                (RecoveryJournal.list root.FullName identity.Scope)
                (Ok
                    {
                        Records = [ { identity with Refused = true } ]
                        Damaged = 0
                    })
                "Refused attempts remain, labelled"

            Expect.isOk
                (RecoveryJournal.save root.FullName identity)
                "Another actual send makes its delivery unconfirmed again"

            Expect.equal
                (RecoveryJournal.list root.FullName identity.Scope)
                (Ok { Records = [ identity ]; Damaged = 0 })
                "Do not carry old refusal knowledge over a new send"

            Expect.isOk
                (RecoveryJournal.markRefused root.FullName identity)
                "Refusal is known again"

            Expect.isOk
                (RecoveryJournal.remove root.FullName identity)
                "Final confirmation clears only its immutable identity regardless of status"

            Expect.equal
                (RecoveryJournal.list root.FullName identity.Scope)
                (Ok { Records = []; Damaged = 0 })
                "Final cleanup"
        finally
            root.Delete true)

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
                let expected = record "10000000-0000-7000-8000-000000000004"

                Expect.equal
                    (RecoveryJournal.list directory expected.Scope)
                    (Ok { Records = [ expected ]; Damaged = 0 })
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

[<Tests>]
let strictFamilies =
    testList
        "native recovery family correlation"
        [
            testCase
                "old journal format errors preserve the owned file for deliberate recovery"
                (fun () ->
                    let root = Directory.CreateTempSubdirectory "claimcore-journal-format-"

                    try
                        let directory = Path.Combine(root.FullName, "journal")
                        let identity = record "10000000-0000-7000-8000-000000000004"

                        Expect.isOk
                            (RecoveryJournal.save directory identity)
                            "Current format publishes"

                        let path = Directory.GetFiles directory |> Array.exactlyOne
                        let old = File.ReadAllText(path).Replace("\"format\":4", "\"format\":3")
                        File.WriteAllText(path, old)

                        let healthy =
                            { identity with
                                AttemptId = Guid.NewGuid()
                            }

                        Expect.isOk
                            (RecoveryJournal.save directory healthy)
                            "A healthy attempt still publishes"

                        Expect.equal
                            (RecoveryJournal.list directory identity.Scope)
                            (Ok { Records = [ healthy ]; Damaged = 1 })
                            "Report the old record while still returning the healthy one"

                        Expect.isError
                            (RecoveryJournal.find directory identity.Scope identity.AttemptId)
                            "Direct lookup refuses the old grammar"

                        Expect.equal
                            (RecoveryJournal.find directory identity.Scope healthy.AttemptId)
                            (Ok(Some healthy))
                            "The damaged record does not block an exact healthy lookup"

                        Expect.equal (File.ReadAllText path) old "No silent conversion or deletion"
                    finally
                        root.Delete true)
        ]

[<Tests>]
let settlementContract =
    testCase "CLI recovery uses the authored shared settlement cases" (fun () ->
        let root = ClaimCore.TestSupport.RepositoryRoot.find ()

        use cases =
            JsonDocument.Parse(
                File.ReadAllBytes(
                    Path.Combine(root, "tests", "fixtures", "operation-settlement.json")
                )
            )

        Expect.equal (cases.RootElement.GetArrayLength()) 7 "The settlement control set is present"

        for item in cases.RootElement.EnumerateArray() do
            let token = item.GetProperty("observation").GetString() |> nonNull
            let kind = Vocabulary.tryParse ReplyKind.vocabulary token |> Option.get
            let settlement = OperationSettlement.ofReply kind

            Expect.equal
                (OperationSettlement.token settlement)
                (item.GetProperty("settlement").GetString() |> nonNull)
                token

            Expect.equal
                (OperationSettlement.isFinal settlement)
                (item.GetProperty("final").GetBoolean())
                token)
