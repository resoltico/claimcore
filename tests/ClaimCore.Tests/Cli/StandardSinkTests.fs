module ClaimCore.Tests.StandardSinkTests

open System
open System.Diagnostics
open System.IO
open System.Security
open System.Text
open Expecto
open ClaimCore.Cli
open ClaimCore.TestSupport

let private buildProbe root directory =
    File.Copy(Path.Combine(root, "global.json"), Path.Combine(directory, "global.json"))

    let reference name (path: string) =
        "<Reference Include=\""
        + name
        + "\"><HintPath>"
        + SecurityElement.Escape(path)
        + "</HintPath></Reference>"

    let project =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference></PropertyGroup><ItemGroup>"
        + reference "ClaimCore.Cli" typeof<RemoteConfiguration>.Assembly.Location
        + reference "FSharp.Core" typeof<unit>.Assembly.Location
        + "<Compile Include=\"OutputProbe.fsx\" /></ItemGroup></Project>"

    File.WriteAllText(Path.Combine(directory, "Probe.fsproj"), project)

    File.Copy(
        Path.Combine(root, "tests/ClaimCore.AcceptanceTests/OutputProbe.fsx"),
        Path.Combine(directory, "OutputProbe.fsx")
    )

    let start =
        ProcessStartInfo(
            "dotnet",
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    [ "build"; "Probe.fsproj"; "--configuration"; "Release"; "--output"; "bin" ]
    |> List.iter start.ArgumentList.Add

    let built = BoundedProcess.run start None 8192 120000
    Expect.equal built.ExitCode 0 "Managed F# output probe compiles"
    Path.Combine(directory, "bin/Probe.dll")

let private bytes root binary methodName mode seed =
    let directory =
        Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName

    match mode with
    | "pipe"
    | "broken" ->
        let result =
            OutputControls.run
                directory
                binary
                methodName
                {
                    Destination = mode
                    Environment = dict []
                }
                [||]

        result, result.StandardOutput
    | "pty" ->
        let result = OutputControls.terminal directory binary methodName
        result, result.StandardOutput
    | _ -> OutputControls.file directory binary methodName mode seed

let private expectedBytes methodName mode seed payload =
    let prefix =
        if List.contains mode [ "append"; "positioned"; "shared-append" ] then
            seed
        else
            [||]

    let copies =
        if methodName = "twice" || methodName = "lifetime" then
            2
        else
            1

    let marker =
        if methodName = "both" then
            "STREAM_SECOND_WRITER\n"
        else
            "STREAM_WRITE_RETURNED\n"

    let suffix =
        if mode.StartsWith("shared", StringComparison.Ordinal) then
            Encoding.UTF8.GetBytes marker
        else
            [||]

    Array.concat [ yield prefix; yield! List.replicate copies payload; yield suffix ]

let private retainUnexpected directory methodName mode (result: BoundedProcessResult) =
    let prefix = Path.Combine(directory, methodName + "-" + mode)

    for name, bytes in [ "stdout", result.StandardOutput; "stderr", result.StandardError ] do
        let path = prefix + "." + name
        File.WriteAllBytes(path, bytes)
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

let private matrix directory binary seed =
    let payload = Encoding.UTF8.GetBytes("Rīga مطالبة 😀\n")

    let modes =
        [
            "pipe"
            "broken"
            "pty"
            "truncate"
            "append"
            "positioned"
            "shared"
            "shared-append"
        ]

    for methodName in [ "selected"; "twice"; "lifetime"; "both" ] do
        for mode in modes do
            let result, actual = bytes directory binary methodName mode seed

            let expected = if mode = "broken" then 70 else 0

            if result.ExitCode <> expected then
                retainUnexpected directory methodName mode result

            if mode = "broken" then
                Expect.equal result.ExitCode 70 (methodName + "/broken surfaces real EPIPE")
            else
                Expect.equal result.ExitCode 0 (methodName + "/" + mode + " completes")

                let expected = expectedBytes methodName mode seed payload

                if actual <> expected then
                    retainUnexpected directory methodName mode result

                Expect.isTrue
                    (actual = expected)
                    (methodName
                     + "/"
                     + mode
                     + " exact Unicode, position, append and shared-offset bytes")

let private controls () =
    let root = RepositoryRoot.find ()
    let directory = Directory.CreateTempSubdirectory("claimcore-standard-sinks-")

    try
        let binary = buildProbe root directory.FullName
        let seed = Encoding.UTF8.GetBytes("SYNTHETIC-PREFIX\n")
        matrix directory.FullName binary seed
        let failure = OutputControls.regularFailure directory.FullName binary

        Expect.contains
            [ 70; 71 ]
            failure.ExitCode
            "Real regular-file write failure remains observable"

        let control, _ = bytes directory.FullName binary "console" "broken" seed
        Expect.equal control.ExitCode 0 "Rejecting control detects Console swallowing EPIPE"

        if OperatingSystem.IsLinux() then
            let full = Path.Combine(directory.FullName, "full")

            let result =
                OutputControls.run
                    full
                    binary
                    "selected"
                    {
                        Destination = "full"
                        Environment = dict []
                    }
                    [||]

            Expect.equal result.ExitCode 70 "Linux ENOSPC remains observable"
    with _ ->
        let retained =
            Path.Combine(root, "artifacts/verification", "output-" + directory.Name)

        Directory.CreateDirectory(Path.GetDirectoryName retained |> nonNull) |> ignore
        Directory.Move(directory.FullName, retained)
        reraise ()

    directory.Delete true

[<Tests>]
let tests =
    testCase "standard sinks preserve native output semantics and expose EPIPE/ENOSPC" controls
