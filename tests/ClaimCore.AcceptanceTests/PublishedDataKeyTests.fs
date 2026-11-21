module ClaimCore.AcceptanceTests.PublishedDataKeyTests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.TestSupport

let private setting = "CLAIMCORE_DATA_KEY_FILE"

let private withKey (values: Dictionary<string, string>) path =
    let copy = Dictionary<string, string>(values)

    match path with
    | None -> copy.Remove setting |> ignore
    | Some keyFile -> copy[setting] <- keyFile

    copy

let private operatorFault (fixture: PublishedFixture.Fixture) path expected =
    let result =
        ProcessRunner.dotnet
            60_000
            fixture.WebBinary
            [ "status" ]
            (withKey fixture.AdministrationEnvironment path)
            None

    Expect.equal result.ExitCode 2 "The administration process rejects the key"

    use document = JsonDocument.Parse(ReadOnlyMemory result.StandardOutput)

    Expect.equal
        (document.RootElement.GetProperty("fault").GetString())
        expected
        "Typed owner fault"

    Expect.equal
        (document.RootElement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
        (set [ "kind"; "fault" ])
        "The administration fault only carries its declared fields"

let private startupFault (fixture: PublishedFixture.Fixture) path expected =
    let result =
        ProcessRunner.dotnet
            60_000
            fixture.WebBinary
            [ "serve" ]
            (withKey fixture.WebEnvironment path)
            None

    Expect.equal result.ExitCode 3 "The published Web host rejects the key"
    let errors = Encoding.UTF8.GetString result.StandardError
    let line = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.last
    use document = JsonDocument.Parse line
    let root = document.RootElement
    Expect.equal (root.GetProperty("kind").GetString()) "startupFailure" "Startup fault envelope"
    Expect.equal (root.GetProperty("code").GetString()) expected "Typed Web fault"

    Expect.equal
        (root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
        (set [ "kind"; "code"; "setting" ])
        "The startup fault only carries its declared fields"

let private rotate () =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()

        let original = fixture.WebEnvironment[setting]
        let replacement = Path.Combine(fixture.Directory, "replacement.data-key")

        PostgresFixture.writeKeyFile replacement

        let missing = Some(Path.Combine(fixture.Directory, "missing.data-key"))
        startupFault fixture None "SETTING_REQUIRED"
        startupFault fixture missing "DATA_KEY_FILE_INVALID"
        startupFault fixture (Some replacement) "RUNTIME_DATA_KEY_INVALID"
        operatorFault fixture None "DATA_KEY_FILE_REQUIRED"
        operatorFault fixture missing "DATA_KEY_FILE_INVALID"
        operatorFault fixture (Some replacement) "DATA_KEY_INVALID"

        let before = fixture.Administration [ "head" ]
        Expect.equal before.ExitCode 0 "Read checkpoint with the original master key"
        use checkpoint = JsonDocument.Parse(ReadOnlyMemory before.StandardOutput)

        fixture.RequestWebStop()
        let! stopped = fixture.WaitForWebExit(TimeSpan.FromSeconds 30.)
        Expect.equal stopped 0 "Stop the old Web process before switching master keys"

        let rotated =
            fixture.Administration [ "rotate-master-key"; "--new-key-file"; replacement ]

        Expect.equal rotated.ExitCode 0 "The owner re-wraps the installation data key"
        use reply = JsonDocument.Parse(ReadOnlyMemory rotated.StandardOutput)

        Expect.equal
            (reply.RootElement.GetProperty("kind").GetString())
            "MASTER_KEY_ROTATED"
            "Rotation receipt"

        Expect.equal
            (reply.RootElement.GetProperty("installationId").GetGuid())
            (checkpoint.RootElement.GetProperty("installationId").GetGuid())
            "Rotation preserves installation identity"

        let former = Path.Combine(fixture.Directory, "former.data-key")
        File.Move(original, former)
        fixture.WebEnvironment[setting] <- replacement
        fixture.AdministrationEnvironment[setting] <- replacement
        do! fixture.RestartWeb()

        let after = fixture.Administration [ "head" ]
        Expect.equal after.ExitCode 0 "Administration can use the new master key"

        Expect.sequenceEqual
            after.StandardOutput
            before.StandardOutput
            "Rotation preserves the ledger checkpoint"

        startupFault fixture (Some former) "RUNTIME_DATA_KEY_INVALID"
        operatorFault fixture (Some former) "DATA_KEY_INVALID"

        fixture.Complete()
    }

[<Tests>]
let tests =
    testList
        "published installation master key"
        [
            testTask "typed refusal and rotation through the real Web and owner processes" {
                do! rotate ()
            }
        ]
