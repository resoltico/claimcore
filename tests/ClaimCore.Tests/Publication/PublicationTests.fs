module ClaimCore.Tests.Publication.PublicationTests

open ClaimCore.CommandLine
open System
open System.IO
open System.Reflection
open System.Diagnostics
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open ClaimCore.TestSupport
open Expecto

let private root = RepositoryRoot.find ()

let private publishedClosure () =
    let directory = Directory.CreateTempSubdirectory("claimcore-client-closure-")

    try
        let dependencies = Path.Combine(directory.FullName, "claimcore.deps.json")
        File.WriteAllText(dependencies, "{\"libraries\":{}}")

        let check () =
            ClaimCore.TestSupport.PublishedClientClosure.violations directory.FullName

        Expect.isEmpty (check ()) "A clean synthetic deliverable is admitted"
        let source = typeof<ClaimCore.Application.IBusinessCalendar>.Assembly.Location
        let target = Path.Combine(directory.FullName, Path.GetFileName source)
        File.Copy(source, target)

        Expect.equal
            (check ())
            [ "ClaimCore.Application" ]
            "A real compiled server assembly is rejected"


    finally
        directory.Delete true

let private nativePublication () =
    let rid = RuntimeInformation.RuntimeIdentifier

    let directory =
        Path.Combine(
            root,
            "artifacts",
            "verification",
            "native-cli-" + Guid.NewGuid().ToString("N")
        )

    Directory.CreateDirectory(
        directory,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    |> ignore

    let mutable passed = false

    let run executable arguments =
        let start =
            ProcessStartInfo(
                executable,
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            )

        CliProcessEnvironment.clearInherited start
        CliProcessEnvironment.isolateProfile start directory

        for argument in arguments do
            start.ArgumentList.Add argument

        let result =
            try
                BoundedProcess.run start None 1_048_576 120000
            with :? BoundedProcessFailure as failure ->
                File.WriteAllBytes(Path.Combine(directory, "last.stdout"), failure.StandardOutput)
                File.WriteAllBytes(Path.Combine(directory, "last.stderr"), failure.StandardError)
                reraise ()

        File.WriteAllBytes(Path.Combine(directory, "last.stdout"), result.StandardOutput)
        File.WriteAllBytes(Path.Combine(directory, "last.stderr"), result.StandardError)

        Expect.equal
            result.ExitCode
            0
            ("Native CLI process succeeds: " + Encoding.UTF8.GetString result.StandardError)

        result.StandardOutput

    try
        let configuration =
            typeof<ClaimCore.Cli.RemoteConfiguration>
                .Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()
            |> nonNull
            |> _.Configuration

        let output = Path.Combine(directory, "publish")

        run
            "dotnet"
            [
                "publish"
                Path.Combine(root, "src", "ClaimCore.Cli", "ClaimCore.Cli.fsproj")
                "--configuration"
                configuration
                "--no-build"
                "--no-restore"
                "--output"
                output
            ]
        |> ignore

        Expect.isEmpty
            (PublishedClientClosure.violations output)
            "The actual publication contains no server dependencies"

        ThirdPartyNotices.qualify root output "ClaimCore.Cli" |> ignore
        let executable = Path.Combine(output, "claimcore")

        use version =
            JsonDocument.Parse((run executable [ "version"; "--json" ]).AsMemory())

        Expect.equal
            (version.RootElement.GetProperty("product").GetString())
            "ClaimCore"
            "The native apphost executes real version discovery"

        let help = run executable [ "help"; "case"; "open" ] |> Encoding.UTF8.GetString

        Expect.stringContains
            help
            "claimcore case open"
            "Offline command help executes from the publication"

        let schema =
            run executable [ "schema"; "operation.execute"; "request" ]
            |> Encoding.UTF8.GetString

        use parsedSchema = JsonDocument.Parse schema

        Expect.equal
            parsedSchema.RootElement.ValueKind
            JsonValueKind.Object
            "The published schema is JSON"

        Expect.stringContains
            schema
            "\"operationId\""
            "The mutation schema retains its identity contract"

        let reminderHelp =
            run executable [ "help"; "reminder"; "list" ] |> Encoding.UTF8.GetString

        Expect.stringContains
            reminderHelp
            "[--case-id "
            "Published reminder syntax retains its optional named ID selector"

        use reminderJson =
            JsonDocument.Parse(
                (run executable [ "help"; "reminder"; "list"; "--format=json" ]).AsMemory()
            )

        let reminderOperation = reminderJson.RootElement.GetProperty("operations")[0]

        Expect.stringContains
            (reminderOperation.GetProperty("synopsis").GetString() |> nonNull)
            "[--case-id "
            "Published machine help includes the same selector"

        let reminderSchema =
            run executable [ "schema"; "reminder.list"; "request" ]
            |> Encoding.UTF8.GetString

        Expect.stringContains
            reminderSchema
            "\"caseId\""
            "Published request discovery still exposes the typed selector"

        for arguments, expected in
            [
                [ "--text"; "-h" ], "-h"
                [ "--text=-h" ], "-h"
                [ "--text=--help" ], "--help"
            ] do
            use literal =
                JsonDocument.Parse(
                    (run executable ([ "people"; "list"; "--dry-run" ] @ arguments)).AsMemory()
                )

            Expect.equal
                (literal.RootElement.GetProperty("endpoint").GetString())
                "people.list"
                "Published executable help detection does not steal named values"

            Expect.equal
                (literal.RootElement.GetProperty("input").GetProperty("text").GetString())
                expected
                "The published actual command retains acquired data"

        for alias in [ "-h"; "--help" ] do
            let help = run executable [ "people"; "list"; alias ] |> Encoding.UTF8.GetString

            Expect.stringContains
                help
                "claimcore people list"
                "Standalone help aliases remain offline discovery"

            use literal =
                JsonDocument.Parse(
                    (run executable [ "case"; "get"; "--dry-run"; "--"; alias ]).AsMemory()
                )

            Expect.equal
                (literal.RootElement
                    .GetProperty("input")
                    .GetProperty("target")
                    .GetProperty("caseReference")
                    .GetString())
                alias
                "The published terminator keeps a help-like operand literal"

        use selected =
            JsonDocument.Parse(
                (run executable [ "case"; "get"; "--dry-run"; "--"; "--claim" ]).AsMemory()
            )

        Expect.equal
            (selected.RootElement
                .GetProperty("input")
                .GetProperty("target")
                .GetProperty("caseReference")
                .GetString())
            "--claim"
            "The published argv boundary preserves an option-looking reference"

        use draft =
            JsonDocument.Parse(
                (run executable [ "note"; "add"; "--dry-run"; "--text=--urgent"; "--"; "--claim" ])
                    .AsMemory()
            )

        let input = draft.RootElement.GetProperty("input")

        Expect.equal
            (input
                .GetProperty("binding")
                .GetProperty("target")
                .GetProperty("caseReference")
                .GetString())
            "--claim"
            "Published offline preparation preserves the selected case"

        Expect.equal
            (input.GetProperty("inputs").GetProperty("input:noteText").GetString())
            "--urgent"
            "Published offline preparation preserves an assigned option-looking value"

        printfn
            "Native CLI publication: RID=%s; configuration=%s; apphost/help/schema/notices/source/closure passed."
            rid
            configuration

        passed <- true
    finally
        if passed then
            Directory.Delete(directory, true)
        else
            Console.Error.WriteLine("Native CLI publication evidence retained: " + directory)

[<Tests>]
let tests =
    testList
        "publication"
        [
            testCase
                "published client closure rejects compiled server dependencies"
                publishedClosure
            testCase
                ("published CLI executes and qualifies its deliverable ("
                 + RuntimeInformation.RuntimeIdentifier
                 + ")")
                nativePublication
        ]
