module ClaimCore.ArchitectureTests.NativeBuildTests

open System
open System.Diagnostics
open System.IO
open System.Security
open System.Runtime.InteropServices
open Expecto
open ClaimCore.TestSupport

let private run executable arguments =
    let start =
        ProcessStartInfo(
            executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    arguments |> List.iter start.ArgumentList.Add
    use child = Process.Start start |> nonNull
    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit 30000) then
        child.Kill true
        invalidOp "Native build guard control timed out."

    child.ExitCode, output.Result + errors.Result

let private ownership () =
    let root = RepositoryRoot.find ()
    let directory = Directory.CreateTempSubdirectory("claimcore-native-build-")

    try
        let target = Path.Combine(root, "Directory.Build.targets") |> SecurityElement.Escape
        let project = Path.Combine(directory.FullName, "ClaimCore.Cli.proj")

        for item, message in
            [
                "ClaimCoreNativeLibrary", "Product native libraries belong only"
                "ClaimCoreNativeProgram", "Native supervisor/control programs belong only"
            ] do
            File.WriteAllText(
                project,
                "<Project><ItemGroup><"
                + item
                + " Include=\"absent.c\" LibraryName=\"forbidden\" ProgramName=\"forbidden\" />"
                + "</ItemGroup><Import Project=\""
                + target
                + "\" /></Project>"
            )

            let code, output =
                run
                    "dotnet"
                    [ "msbuild"; project; "-nologo"; "-target:ClaimCoreValidateNativeHost" ]

            Expect.notEqual code 0 "An unauthorized native owner refuses before compilation"
            Expect.stringContains output message "The ownership guard causes the refusal"
    finally
        directory.Delete true

let private nativeFixture directory =
    let file name = Path.Combine(directory, name)
    let escape = SecurityElement.Escape
    let source = file "main.c"
    let owner = file "native.props"
    let project = file "ClaimCore.Tests.proj"
    File.WriteAllText(source, "int main(void) { return 0; }\n")

    File.WriteAllText(
        owner,
        "<Project><ItemGroup><ClaimCoreNativeProgram Include=\""
        + escape source
        + "\" ProgramName=\"control\" /></ItemGroup></Project>"
    )

    let target = Path.Combine(RepositoryRoot.find (), "Directory.Build.targets")

    File.WriteAllText(
        project,
        "<Project><PropertyGroup><ArtifactsPath>"
        + escape directory
        + "</ArtifactsPath><Configuration>Release</Configuration><NETCoreSdkRuntimeIdentifier>"
        + RuntimeInformation.RuntimeIdentifier
        + "</NETCoreSdkRuntimeIdentifier></PropertyGroup>"
        + "<Import Project=\""
        + escape owner
        + "\" /><Import Project=\""
        + escape target
        + "\" /></Project>"
    )

    let output =
        Path.Combine(
            directory,
            "native",
            "ClaimCore.Tests",
            RuntimeInformation.RuntimeIdentifier,
            "Release",
            "control"
        )

    project, owner, output

let private incrementalDefinitions () =
    let directory = Directory.CreateTempSubdirectory("claimcore-native-definition-")

    try
        let project, owner, output = nativeFixture directory.FullName

        let compile () =
            let code, details =
                run "dotnet" [ "msbuild"; project; "-nologo"; "-target:ClaimCoreBuildNative" ]

            Expect.equal code 0 ("The actual native control compiles: " + details)

        compile ()
        let first = File.GetLastWriteTimeUtc output
        compile ()
        Expect.equal (File.GetLastWriteTimeUtc output) first "Unchanged inputs skip compilation"
        File.AppendAllText(owner, "\n")
        File.SetLastWriteTimeUtc(owner, first.AddSeconds 2.)
        compile ()

        Expect.isGreaterThan
            (File.GetLastWriteTimeUtc output)
            first
            "The declaring import invalidates the native output"
    finally
        directory.Delete true

[<Tests>]
let tests =
    testList
        "native build boundaries"
        [
            testCase "product native items and test programs reject unauthorized owners" ownership
            testCase
                "native declaration imports invalidate outputs while unchanged inputs skip"
                incrementalDefinitions
            testCase "cross-RID compilation refuses without creating an output" (fun () ->
                let directory = Directory.CreateTempSubdirectory("claimcore-native-rid-")

                try
                    let stamp = Path.Combine(directory.FullName, "toolchain.txt")

                    let code, output =
                        run
                            "bash"
                            [
                                Path.Combine(RepositoryRoot.find (), "eng/build-native.sh")
                                "identify"
                                "unsupported-rid"
                                stamp
                            ]

                    Expect.equal code 65 "A mismatched native target refuses"

                    Expect.stringContains
                        output
                        "cross-RID builds are unsupported"
                        "Actionable target refusal"

                    Expect.isFalse (File.Exists stamp) "No mislabeled artifact or stamp"
                finally
                    directory.Delete true)
        ]
