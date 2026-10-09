module internal ClaimCore.AcceptanceTests.NativeOwnerWorkspaceProof

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.AcceptanceTests.NativeRoleFixture

let private grants active =
    let inputs = RemoteFixture.inputs.Value

    let subject =
        File.ReadAllText(Path.Combine(inputs.PrivateDirectory, "owner.subject")).TrimEnd('\r', '\n')

    [ "CASE_EDITOR"; "RECOVERY_OPERATOR"; "RECOVERY_EXPORTER" ]
    |> List.map (fun role ->
        RemoteFixture.encode
            "authority.setGrant"
            {|
                eventId = RemoteFixture.id ()
                principal =
                    {|
                        kind = "HUMAN"
                        issuer = inputs.Issuer
                        subject = subject
                    |}
                role = role
                scope = {| kind = "INSTALLATION" |}
                active = active
            |})

let qualify () =
    let reference = "OWNER-ONLY-" + RemoteFixture.id ()
    seed reference |> ignore

    let reads =
        [
            RemoteFixture.encode "case.get" {| caseReference = reference |}
            RemoteFixture.encode "case.list" {| limit = 10 |}
        ]

    let revoked, restored = grants false, grants true
    let frames = revoked @ reads @ restored
    let result = RemoteFixture.interactiveSession frames

    Expect.equal
        result.ExitCode
        0
        "Owner-only native session completed and restored exact known grants"

    Expect.equal result.StandardError.Length 0 "No private diagnostics"

    let lines =
        Encoding.UTF8
            .GetString(result.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)

    Expect.equal lines.Length frames.Length "Every owner grant and read has a frame"

    for index in
        [ 0 .. revoked.Length - 1 ]
        @ [ revoked.Length + reads.Length .. frames.Length - 1 ] do
        use reply = JsonDocument.Parse(lines[index])
        Expect.equal (RemoteFixture.tag reply) "APPLIED" "Known role changes are confirmed"

    use denied = JsonDocument.Parse(lines[revoked.Length])

    Expect.equal
        (RemoteFixture.tag denied)
        "REJECTED"
        "Owner-only does not gain business read permission"

    use listed = JsonDocument.Parse(lines[revoked.Length + 1])

    Expect.equal
        (RemoteFixture.tag listed)
        "SUCCEEDED"
        "Enabled owner retains bounded filtered listing"

    Expect.equal
        (listed.RootElement
            .GetProperty("service")
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("availableCommands")
            .GetArrayLength())
        0
        "Owner-only is not offered OPEN"

let tests =
    [
        testCase
            "[CC-AUTH-001][CC-CLI-001] owner-only native workspace does not imply case authority"
            qualify
    ]
