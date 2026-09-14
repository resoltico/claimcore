module ClaimCore.Tests.ClientProtocolBoundaryTests

open System
open System.IO
open Expecto
open ClaimCore.TestSupport

let private consumerDirectory () =
    Path.Combine(RepositoryRoot.find (), "artifacts/bin/ClaimCore.ProtocolConsumer/release")

let private runConsumer directory =
    let path = Path.Combine(directory, "ClaimCore.ProtocolConsumer.dll")
    Expect.isTrue (File.Exists(path)) "Ordinary consumer was built by the solution"
    let result = CliProcessTests.runDotnet [ path ] ""

    Expect.equal
        result.ExitCode
        0
        "Independent client encodes and decodes without starting a service"

    Expect.equal result.StandardOutput "" "No incidental output or credentials"
    Expect.equal result.StandardError "" "No loader dependency failures"

    let names =
        Directory.GetFiles(directory, "ClaimCore.*.dll")
        |> Array.map Path.GetFileName
        |> Set.ofArray

    Expect.equal
        names
        (set [ "ClaimCore.Protocol.dll"; "ClaimCore.ProtocolConsumer.dll" ])
        "Client runtime closure contains no server implementation"

let private ordinaryConsumer () =
    let directory =
        Path.Combine(
            Path.GetTempPath(),
            "claimcore-protocol-publish-" + Guid.NewGuid().ToString("N")
        )

    let project =
        Path.Combine(
            RepositoryRoot.find (),
            "tests/ClaimCore.ProtocolConsumer/ClaimCore.ProtocolConsumer.fsproj"
        )

    try
        let published =
            CliProcessTests.runDotnet
                [
                    "publish"
                    project
                    "--configuration"
                    "Release"
                    "--no-build"
                    "--no-restore"
                    "--output"
                    directory
                ]
                ""

        Expect.equal published.ExitCode 0 "Independent client publication succeeds"
        runConsumer directory
    finally
        if Directory.Exists directory then
            Directory.Delete(directory, true)

let private compileProbe source =
    let directory =
        Path.Combine(
            Path.GetTempPath(),
            "claimcore-protocol-consumer-" + Guid.NewGuid().ToString("N")
        )

    Directory.CreateDirectory(directory) |> ignore

    try
        let file = Path.Combine(directory, "consumer.fsx")

        let reference =
            Path.Combine(consumerDirectory (), "ClaimCore.Protocol.dll").Replace("\"", "\"\"")

        File.WriteAllText(file, "#r @\"" + reference + "\"\n" + source)
        CliProcessTests.runDotnet [ "fsi"; "--nologo"; "--exec"; file ] ""
    finally
        Directory.Delete(directory, true)

let private noServerSurface () =
    let valid =
        compileProbe
            "let input: ClaimCore.Protocol.CaseGetRequest = { CaseReference = \"SYNTHETIC\" }"

    Expect.equal valid.ExitCode 0 "The ordinary-consumer reference is valid"

    for symbol in [ "Domain.Claim"; "Application.IClaimsCore"; "Hosting.Runtime" ] do
        let invalid = compileProbe ("let value = typeof<ClaimCore." + symbol + ">")

        Expect.notEqual
            invalid.ExitCode
            0
            "Protocol reference cannot introduce native core execution"

        Expect.stringContains
            (invalid.StandardOutput + invalid.StandardError)
            "FS0039"
            "Failure is missing native symbol, not a broken compiler"

let tests =
    testList
        "client protocol ordinary consumer"
        [
            testCase
                "independent consumer runtime contains only client-safe assemblies"
                ordinaryConsumer
            testCase "ordinary protocol callers cannot compile native server access" noServerSurface
        ]
