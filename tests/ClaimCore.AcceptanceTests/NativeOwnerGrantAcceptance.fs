module ClaimCore.AcceptanceTests.NativeOwnerGrantAcceptance

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto

let private requests active =
    let binding =
        Path.Combine(RemoteFixture.inputs.Value.PrivateDirectory, "initial-owner.json")

    use document = JsonDocument.Parse(ReadOnlyMemory(File.ReadAllBytes binding))
    let owner = document.RootElement

    let expectedSubject =
        File
            .ReadAllText(Path.Combine(RemoteFixture.inputs.Value.PrivateDirectory, "owner.subject"))
            .TrimEnd('\r', '\n')

    Expect.equal
        (owner.GetProperty("issuer").GetString())
        RemoteFixture.inputs.Value.Issuer
        "Existing issuer binding"

    Expect.equal (owner.GetProperty("subject").GetString()) expectedSubject "Existing owner binding"

    let principal =
        {|
            kind = "HUMAN"
            issuer = owner.GetProperty("issuer").GetString()
            subject = owner.GetProperty("subject").GetString()
        |}

    [ "CASE_EDITOR"; "RECOVERY_OPERATOR"; "RECOVERY_EXPORTER" ]
    |> List.map (fun role ->
        let eventId = RemoteFixture.id ()

        let frame =
            RemoteFixture.encode
                "authority.setGrant"
                {|
                    eventId = eventId
                    principal = principal
                    role = role
                    scope = {| kind = "INSTALLATION" |}
                    active = active
                |}

        eventId, frame)

let private applied result line endpoint eventId =
    use document =
        RemoteFixture.parse
            0
            endpoint
            { result with
                StandardOutput = Encoding.UTF8.GetBytes(line: string)
            }

    Expect.equal (RemoteFixture.tag document) "APPLIED" "Grant evidence settled"

    let data =
        document.RootElement.GetProperty("service").GetProperty("outcome").GetProperty("data")

    Expect.equal (data.GetProperty("eventId").GetString()) eventId "Exact authority operation"
    data.GetProperty("grantRevision").GetString(), data.GetProperty("targetActorId").GetString()

let private readBackGrant result (lines: string array) index (eventId, _) =
    let first = applied result lines[index * 3] "authority.setGrant" eventId
    let replay = applied result lines[index * 3 + 1] "authority.setGrant" eventId
    let observed = applied result lines[index * 3 + 2] "authority.observe" eventId
    Expect.equal replay first "Exact replay preserves applied grant evidence"
    Expect.equal observed first "Observation returns the same accepted authority event"
    snd first

let private refused result line =
    use document =
        RemoteFixture.parse
            0
            "authority.setGrant"
            { result with
                StandardOutput = Encoding.UTF8.GetBytes(line: string)
            }

    Expect.equal
        (RemoteFixture.tag document)
        "RESOURCE_UNAVAILABLE"
        "A fresh event cannot reaffirm an already active grant"

let private caseworkAvailable result line =
    use listed =
        RemoteFixture.parse
            0
            "case.list"
            { result with
                StandardOutput = Encoding.UTF8.GetBytes(line: string)
            }

    Expect.equal (RemoteFixture.tag listed) "SUCCEEDED" "Existing owner can list cases"

let qualify () =
    let unchanged = requests true

    let transitions =
        List.zip (requests false) (requests true)
        |> List.collect (fun (revocation, grant) -> [ revocation; grant ])

    let frames =
        transitions
        |> List.collect (fun (eventId, frame) ->
            [
                frame
                frame
                RemoteFixture.encode "authority.observe" {| eventId = eventId |}
            ])

    let result =
        RemoteFixture.interactiveSession (
            (unchanged |> List.map snd)
            @ frames
            @ [ RemoteFixture.encode "case.list" {| limit = 1 |} ]
        )

    Expect.equal result.ExitCode 0 "Authenticated native grant session"
    Expect.equal result.StandardError.Length 0 "No private diagnostics"

    let lines =
        Encoding.UTF8
            .GetString(result.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)

    Expect.equal
        lines.Length
        (unchanged.Length + frames.Length + 1)
        "Every exact frame has a result"

    lines |> Array.take unchanged.Length |> Array.iter (refused result)

    let settled = lines |> Array.skip unchanged.Length

    let actors = transitions |> List.mapi (readBackGrant result settled)

    Expect.equal
        (actors |> List.distinct |> List.length)
        1
        "Every grant targets the same existing owner"

    caseworkAvailable result lines[lines.Length - 1]
