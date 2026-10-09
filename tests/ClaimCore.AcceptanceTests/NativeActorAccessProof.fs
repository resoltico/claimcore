module internal ClaimCore.AcceptanceTests.NativeActorAccessProof

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.AcceptanceTests.NativeRoleFixture

let private refused username reference =
    let frames =
        [
            RemoteFixture.encode "case.get" {| caseReference = reference |}
            RemoteFixture.encode "case.list" {| limit = 10 |}
        ]

    let result = RemoteFixture.interactiveSessionAs username frames
    Expect.equal result.ExitCode 0 "Authenticated native refusal remains frame-local"
    Expect.equal result.StandardError.Length 0 "No private authentication diagnostics"

    let lines =
        Encoding.UTF8
            .GetString(result.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)

    Expect.equal lines.Length frames.Length "Every authenticated read has a response"

    for line in lines do
        use reply = JsonDocument.Parse(line)

        Expect.equal
            (RemoteFixture.tag reply)
            "REJECTED"
            "Inactive or unknown actor cannot disclose rows"

let private enableFrame enabled =
    let source = RemoteFixture.inputs.Value

    let subject =
        File
            .ReadAllText(Path.Combine(source.PrivateDirectory, "steward.subject"))
            .TrimEnd('\r', '\n')

    RemoteFixture.encode
        "authority.setEnabled"
        {|
            eventId = RemoteFixture.id ()
            principal =
                {|
                    kind = "HUMAN"
                    issuer = source.Issuer
                    subject = subject
                |}
            enabled = enabled
        |}

let private disabled () =
    let reference = "DISABLED-NATIVE-" + RemoteFixture.id ()
    seed reference |> ignore
    configure reference "CASE_READER" false
    confirmedGrants [ enableFrame false ]

    try
        refused "synthetic-steward" reference
    finally
        confirmedGrants [ enableFrame true ]
        restore reference "CASE_READER" false

let private unregistered () =
    let reference = "UNKNOWN-NATIVE-" + RemoteFixture.id ()
    seed reference |> ignore
    refused "synthetic-outsider" reference

let tests =
    [
        testCase
            "[CC-AUTH-001][CC-CLI-001] disabled native reader remains refused despite its role"
            disabled
        testCase
            "[CC-AUTH-001][CC-CLI-001] unregistered native human authenticates without row access"
            unregistered
    ]
