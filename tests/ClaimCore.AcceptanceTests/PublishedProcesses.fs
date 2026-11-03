module internal ClaimCore.AcceptanceTests.PublishedProcesses

open System
open System.Diagnostics
open System.Collections.Generic
open System.IO
open System.Threading
open Expecto

let start executable arguments (environment: IReadOnlyDictionary<string, string>) =
    let value =
        ProcessStartInfo(
            executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    ClaimCore.TestSupport.CliProcessEnvironment.clearInherited value

    for argument in arguments do
        value.ArgumentList.Add argument

    for pair in environment do
        value.Environment[pair.Key] <- pair.Value

    value.Environment["DOTNET_NOLOGO"] <- "1"
    value.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
    value

let run timeout value input =
    ClaimCore.TestSupport.BoundedProcess.run value input 1_048_576 timeout

let private retainPublishFailure directory name stdout stderr =
    Directory.CreateDirectory(
        directory,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    |> ignore

    FixtureFiles.write directory ("publish-" + name + ".stdout") stdout |> ignore
    FixtureFiles.write directory ("publish-" + name + ".stderr") stderr |> ignore
    Console.Error.WriteLine("Publish diagnostics retained privately: " + directory)

let publish root directory name =
    let output = Path.Combine(directory, name)

    let args =
        [
            "publish"
            Path.Combine(root, "src", name, name + ".fsproj")
            "--configuration"
            "Release"
            "--no-build"
            "--no-restore"
            "--output"
            output
        ]

    let result =
        try
            run 120_000 (start "dotnet" args (Dictionary<string, string>())) None
        with :? ClaimCore.TestSupport.BoundedProcessFailure as failure ->
            ClaimCore.TestSupport.FixtureDisposal.afterFailure
                failure
                [
                    fun () ->
                        retainPublishFailure
                            directory
                            name
                            failure.StandardOutput
                            failure.StandardError
                ]

            reraise ()

    try
        Expect.equal
            result.ExitCode
            0
            ("Publish "
             + name
             + "; build the current Release inputs first. Private failure diagnostics: "
             + directory)
    with failure ->
        ClaimCore.TestSupport.FixtureDisposal.afterFailure
            failure
            [
                fun () ->
                    retainPublishFailure directory name result.StandardOutput result.StandardError
            ]

    ThirdPartyNotices.qualify root output name |> ignore

    if name = "ClaimCore.Cli" then
        Expect.isEmpty
            (ClaimCore.TestSupport.PublishedClientClosure.violations output)
            "Published CLI excludes server assemblies and resolved dependencies"

    Path.Combine(output, ThirdPartyNoticeAssets.assemblyName root name + ".dll")

let publicationFailure () =
    let directory = FixtureFiles.directory ()

    try
        Expect.throws
            (fun () ->
                publish (Path.Combine(directory, "missing-source")) directory "ClaimCore.Missing"
                |> ignore)
            "The missing owned project cannot be published"

        let stdout = Path.Combine(directory, "publish-ClaimCore.Missing.stdout")
        let stderr = Path.Combine(directory, "publish-ClaimCore.Missing.stderr")
        let files = [ stdout; stderr ]

        for path in files do
            Expect.isTrue (File.Exists path) "Each bounded output stream is retained"

            Expect.isLessThanOrEqual
                (FileInfo(path).Length)
                1_048_576L
                "Existing process output bound remains in force"

            Expect.equal
                (File.GetUnixFileMode path)
                (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                "Publish diagnostics are private files"

        Expect.isGreaterThan
            (files |> List.sumBy (fun path -> FileInfo(path).Length))
            0L
            "The actual failed SDK invocation leaves actionable evidence"

        Expect.stringContains
            (files |> List.map File.ReadAllText |> String.concat "\n")
            "MSB1009"
            "The missing-project refusal is retained without requiring a database fixture"
    finally
        Directory.Delete(directory, true)

type Web(binary, environment, directory, ?logPrefix: string) =
    let logPrefix = defaultArg logPrefix "web"
    let value = start "dotnet" [ binary ] environment
    let child = Process.Start value |> nonNull
    let deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30.))

    let output =
        ClaimCore.TestSupport.BoundedProcess.read 8_388_608 child.StandardOutput.BaseStream deadline

    let errors =
        ClaimCore.TestSupport.BoundedProcess.read 1_048_576 child.StandardError.BaseStream deadline

    member _.HasExited = child.HasExited

    member _.RequestStop() =
        let signal =
            start "kill" [ "-TERM"; string child.Id ] (Dictionary<string, string>())

        let result = run 5000 signal None
        Expect.equal result.ExitCode 0 "Signal only the owned published Web process"

    member _.WaitForExit(bound: TimeSpan) =
        task {
            use deadline = new CancellationTokenSource(bound)
            do! child.WaitForExitAsync(deadline.Token)
            return child.ExitCode
        }

    interface IDisposable with
        member _.Dispose() =
            try
                if not child.HasExited then
                    child.Kill(true)

                child.WaitForExit()

                FixtureFiles.write
                    directory
                    (logPrefix + ".stdout")
                    (output.GetAwaiter().GetResult())
                |> ignore

                FixtureFiles.write
                    directory
                    (logPrefix + ".stderr")
                    (errors.GetAwaiter().GetResult())
                |> ignore
            finally
                deadline.Dispose()
                child.Dispose()
