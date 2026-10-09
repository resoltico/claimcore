module internal ClaimCore.AcceptanceTests.NativeRoleFixture

open System
open System.IO
open System.Text
open System.Text.Json
open System.Collections.Generic
open Expecto

let outcome endpoint input =
    use reply = RemoteFixture.call endpoint input |> RemoteFixture.parse 0 endpoint
    reply.RootElement.GetProperty("service").GetProperty("outcome").Clone()

let command reference revision kind values =
    {|
        operationId = RemoteFixture.id ()
        caseReference = reference
        expectedRevision = revision
        command = {| kind = kind; values = values |}
    |}

let seed reference =
    let opened =
        outcome
            "command.execute"
            (command
                reference
                "0"
                "OPEN"
                {|
                    incidentDate = "2026-09-01"
                    incidentNotificationDate = "2026-09-02"
                    incidentCountry = "Latvia"
                    claimantName = "Synthetic narrow claimant"
                    insurerName = "Synthetic narrow insurer"
                    claimedAmount = "10.0000"
                    claimedCurrency = "EUR"
                |})

    Expect.equal (opened.GetProperty("tag").GetString()) "COMPLETED" "Fresh isolated case accepted"

    Expect.equal
        (opened.GetProperty("data").GetProperty("execution").GetProperty("tag").GetString())
        "ACCEPTED"
        "Independent seed was accepted"

    let request = command reference "1" "CLOSE" {| |}
    let prepared = outcome "command.prepare" request

    Expect.equal
        (prepared.GetProperty("tag").GetString())
        "PREPARED"
        "Exact retained CLOSE is available"

    request.operationId,
    prepared
        .GetProperty("data")
        .GetProperty("details")
        .GetProperty("summary")
        .GetProperty("requestSha256")
        .GetString()

let grantFrame reference role scoped active =
    let inputs = RemoteFixture.inputs.Value

    let subject =
        File
            .ReadAllText(Path.Combine(inputs.PrivateDirectory, "steward.subject"))
            .TrimEnd('\r', '\n')

    let scope = Dictionary<string, string>()
    scope["kind"] <- (if scoped then "CASE" else "INSTALLATION")

    if scoped then
        scope["caseReference"] <- reference

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
            scope = scope
            active = active
        |}

let confirmedGrants (frames: string list) =
    let result = RemoteFixture.interactiveSession frames
    Expect.equal result.ExitCode 0 "Owner native authority session completed"
    Expect.equal result.StandardError.Length 0 "Authority diagnostics remain private"

    let lines =
        Encoding.UTF8
            .GetString(result.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)

    Expect.equal lines.Length frames.Length "Every exact authority frame has a reply"

    for line in lines do
        use reply = JsonDocument.Parse(line)

        Expect.equal
            (RemoteFixture.tag reply)
            "APPLIED"
            "Only confirmed authority changes drive the fixture"

let configure reference role scoped =
    if role <> "DATA_STEWARD" then
        let grants = [ grantFrame reference "DATA_STEWARD" false false ]

        let grants =
            if role = "NONE" then
                grants
            else
                grants @ [ grantFrame reference role scoped true ]

        confirmedGrants grants

let restore reference role scoped =
    if role <> "DATA_STEWARD" then
        let grants =
            if role = "NONE" then
                []
            else
                [ grantFrame reference role scoped false ]

        confirmedGrants (grants @ [ grantFrame reference "DATA_STEWARD" false true ])

let frameRead endpoint input = RemoteFixture.encode endpoint input

let narrowReplies (frames: string list) =
    let result = RemoteFixture.interactiveSessionAs "synthetic-steward" frames
    Expect.equal result.ExitCode 0 "Selected human native session completed"
    Expect.equal result.StandardError.Length 0 "No private diagnostics"

    let lines =
        Encoding.UTF8
            .GetString(result.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)

    Expect.equal lines.Length frames.Length "No session frame was lost"

    lines
    |> Array.map (fun line ->
        use reply = JsonDocument.Parse(line)
        reply.RootElement.GetProperty("service").GetProperty("outcome").Clone())
