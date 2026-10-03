module ClaimCore.Tests.NativeFileBoundaryTests

open System
open System.Diagnostics
open System.IO
open System.Text
open Expecto
open ClaimCore.HostSecurity
open ClaimCore.TestSupport

let private fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
let private directoryMode = fileMode ||| UnixFileMode.UserExecute

let private withSandbox action =
    let temporary = Path.GetTempPath()

    let physical =
        if OperatingSystem.IsMacOS() then
            "/private" + temporary
        else
            temporary

    let root =
        Path.Combine(physical, "claimcore-native-boundary-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(root, directoryMode) |> ignore

    try
        action root
    finally
        Directory.Delete(root, true)

let private refused result =
    Expect.equal
        result
        (Error PrivateFileFailure.AccessRefused)
        "Malformed paths refuse before native access."

let private malformedLeaves () =
    if OperatingSystem.IsWindows() then
        Expect.equal
            (PrivateFileService.readBinary 64 "unsupported")
            (Error PrivateFileFailure.UnsupportedPlatform)
            "Windows remains fail-closed."
    else
        withSandbox (fun root ->
            for bad, encoded in
                [
                    string (char 0xD800), "\uFFFD"
                    string (char 0xDC00), "\uFFFD"
                    String([| char 0xD800; char 0xD800 |]), "\uFFFD\uFFFD"
                ] do
                let valid = Path.Combine(root, "source-" + encoded)
                File.WriteAllText(valid, "synthetic")
                File.SetUnixFileMode(valid, fileMode)
                let invalid = Path.Combine(root, "source-" + bad)
                PrivateFileService.readBinary 64 invalid |> refused
                PrivateFileService.hashPrivateFile 64L invalid |> refused
                PrivateFileService.deletePrivate invalid |> refused

                match PrivateFileService.openExclusive invalid with
                | Ok handle ->
                    handle.Dispose()
                    failtest "Malformed path acquired an alias lock."
                | Error error -> refused (Error error: Result<unit, PrivateFileFailure>)

                Expect.equal
                    (File.ReadAllText(valid))
                    "synthetic"
                    "Encoded alias remains untouched."

                let destination = Path.Combine(root, "new-" + bad)
                PrivateFileService.writeNew 64 destination [| 0x41uy |] |> refused

                Expect.isFalse
                    (File.Exists(Path.Combine(root, "new-" + encoded)))
                    "No alias export is created."

                PrivateFileService.ensureDirectory destination |> refused

                Expect.isFalse
                    (Directory.Exists(Path.Combine(root, "new-" + encoded)))
                    "No alias directory is created.")

let private malformedAncestors () =
    if OperatingSystem.IsWindows() then
        Expect.equal
            (PrivateFileService.requirePrivateDirectory "unsupported")
            (Error PrivateFileFailure.UnsupportedPlatform)
            "Windows remains fail-closed."
    else
        withSandbox (fun root ->
            let valid = Path.Combine(root, "parent-\uFFFD")
            Directory.CreateDirectory(valid, directoryMode) |> ignore
            let invalid = Path.Combine(root, "parent-" + string (char 0xD800))
            PrivateFileService.requirePrivateDirectory invalid |> refused

            PrivateFileService.writeNew 64 (Path.Combine(invalid, "new")) [| 0x41uy |]
            |> refused

            Expect.isFalse
                (File.Exists(Path.Combine(valid, "new")))
                "An aliased ancestor receives no export."

            let exact = Path.Combine(valid, "joining-\u200C-\U0001F512")

            PrivateFileService.writeNew 64 exact [| 0x41uy |]
            |> fun result -> Expect.equal result (Ok()) "Valid Unicode remains supported."

            Expect.equal
                (PrivateFileService.readBinary 64 exact)
                (Ok [| 0x41uy |])
                "The exact valid filename reads back.")

let private run command arguments =
    let start = ProcessStartInfo(command)
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true

    for argument in arguments do
        start.ArgumentList.Add(argument)

    BoundedProcess.run start None 65536 60000

let private compileFaultLibrary repository root =
    let source = Path.Combine(repository, "tests/ClaimCore.Tests/NativeHandleFailure.c")
    let includePath = Path.Combine(repository, "src/ClaimCore.HostSecurity/native")

    let extension, link =
        if OperatingSystem.IsMacOS() then
            ".dylib", "-dynamiclib"
        else
            ".so", "-shared"

    let library = Path.Combine(root, "fault" + extension)

    let compiled =
        run
            "cc"
            [
                "-std=c11"
                "-O2"
                "-fPIC"
                "-Wall"
                "-Wextra"
                "-Werror"
                link
                "-I"
                includePath
                source
                "-o"
                library
            ]

    Expect.equal compiled.ExitCode 0 "The isolated native fault fixture compiles."
    library

let private metadataFailureReleasesLock () =
    if OperatingSystem.IsWindows() then
        Expect.equal
            (PrivateFileService.openExclusive "unsupported"
             |> Result.map (fun handle -> handle.Dispose()))
            (Error PrivateFileFailure.UnsupportedPlatform)
            "Windows remains fail-closed."
    else
        withSandbox (fun root ->
            let repository = RepositoryRoot.find ()
            let library = compileFaultLibrary repository root

            let script =
                Path.Combine(repository, "tests/ClaimCore.Tests/NativeHandleFailureProbe.fsx")

            let reference = "--reference:" + typeof<PrivateFileFailure>.Assembly.Location

            let result =
                run
                    "dotnet"
                    [
                        "fsi"
                        reference
                        "--exec"
                        script
                        Path.Combine(root, "fault.lock")
                        library
                    ]

            Expect.equal
                result.ExitCode
                0
                "Metadata refusal releases the kernel lock immediately without GC.")

let tests =
    testList
        "native file boundary"
        [
            testCase
                "[CC-CLI-002] malformed filename scalars cannot read write hash delete or lock encoded aliases"
                malformedLeaves
            testCase
                "[CC-CLI-002] malformed ancestors refuse while valid Unicode filenames remain exact"
                malformedAncestors
            testCase
                "[CC-CLI-002] native metadata failure releases an acquired file lock before refusal"
                metadataFailureReleasesLock
        ]
