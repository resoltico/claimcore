module ClaimCore.Tests.RemoteInvocationTests

open System
open System.Text
open Expecto
open ClaimCore.Cli

let private decode (source: string) =
    match StrictJson.parseDocument 131072 (Encoding.UTF8.GetBytes(source)) with
    | Error reason -> Error reason
    | Ok document ->
        use value = document

        CliRemoteInvocation.decode value.RootElement
        |> Result.map (fun (id, _, _) -> id)

let private basicEndpoint () =
    Expect.equal
        (decode """{"protocolVersion":4,"endpoint":"case.list","input":{"limit":1}}""")
        (Ok "case.list")
        "CLI v4 uses a generated service endpoint"

    Expect.isError
        (decode """{"protocolVersion":3,"endpoint":"case.list","input":{"limit":1}}""")
        "Old CLI framing is refused"

    Expect.isError
        (decode """{"protocolVersion":4,"endpoint":"case.list","input":{"limit":0}}""")
        "Generated input bounds are enforced before service access"

let private handoffEndpoint digest =
    let handoffInput =
        $"""{{
          "approvalId":"10000000-0000-4000-8000-000000000001",
          "handoffId":"20000000-0000-4000-8000-000000000001",
          "oldGeneration":"1",
          "expectedWitnessSequence":"3",
          "expectedWitnessHash":"{digest}",
          "newCapabilitySha256":"{digest}",
          "checkpointSigningKeyId":"30000000-0000-4000-8000-000000000001",
          "fenceReportSha256":"{digest}",
          "inventorySha256":"{digest}",
          "expiresAt":"2026-10-01T00:00:00.0000000+00:00"
        }}"""

    Expect.equal
        (decode
            $"""{{"protocolVersion":4,"endpoint":"authority.approveWriterHandoff","input":{handoffInput}}}""")
        (Ok "authority.approveWriterHandoff")
        "CLI v4 admits the exact actor-bound handoff approval body"

    Expect.isError
        (decode
            $"""{{"protocolVersion":4,"endpoint":"authority.approveWriterHandoff","input":{handoffInput},"timeoutMs":1000}}""")
        "A potentially committed handoff approval cannot have a caller timeout"

let private terminalEndpoint digest =
    let terminalCopy =
        $"""{{
          "eventId":"40000000-0000-4000-8000-000000000001",
          "caseId":"50000000-0000-4000-8000-000000000001",
          "expectedAuthorityRevision":"2",
          "expectedAuthorityHash":"{digest}",
          "installationId":"70000000-0000-4000-8000-000000000001",
          "lineageId":"80000000-0000-4000-8000-000000000001",
          "witnessEpoch":"1",
          "pruneEventId":"60000000-0000-4000-8000-000000000001",
          "witnessCutoffSequence":"12",
          "witnessCutoffHash":"{digest}",
          "copyInventoryDigest":"{digest}",
          "relevantCopyCount":"0",
          "expectedWriterGeneration":"1",
          "policyId":"synthetic-policy-1",
          "suppressionUntil":"2026-10-01T00:00:00.0000000+00:00",
          "validUntil":"2026-09-30T00:00:00.0000000+00:00"
        }}"""

    let terminalInput =
        $"""{{"proposal":{{"kind":"CONFIRM_MANAGED_PAYLOAD_ABSENCE","copy":{terminalCopy}}},"approvalId":"90000000-0000-4000-8000-000000000001","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""

    Expect.equal
        (decode
            $"""{{"protocolVersion":4,"endpoint":"tombstone.approveTerminal","input":{terminalInput}}}""")
        (Ok "tombstone.approveTerminal")
        "CLI v4 admits only the exact steward terminal draft shape"

    Expect.isError
        (decode
            $"""{{"protocolVersion":4,"endpoint":"tombstone.approveTerminal","input":{terminalInput},"timeoutMs":1000}}""")
        "An uncertain terminal approval cannot have a caller timeout"

let private generatedEndpoints () =
    basicEndpoint ()
    let digest = String.replicate 64 "a"
    handoffEndpoint digest
    terminalEndpoint digest

let tests =
    testList
        "remote invocation contract"
        [
            testCase
                "[CC-CLI-003] CLI v4 inputs bind exact noncancellable endpoints"
                generatedEndpoints
        ]
