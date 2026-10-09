module internal ClaimCore.AcceptanceTests.NativeRoleExportProof

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.AcceptanceTests.NativeRoleFixture

let private inspectRefusal operation =
    let denied =
        narrowReplies
            [
                RemoteFixture.encode
                    "recovery.inspect"
                    {|
                        operationId = operation
                        attemptLimit = 10
                    |}
            ]

    Expect.equal
        (denied[0].GetProperty("tag").GetString())
        "REJECTED"
        "Exporter-only never acquires inspection"

let qualify operation digest =
    let destination =
        Path.Combine(
            RemoteFixture.inputs.Value.PrivateDirectory,
            "narrow-export-" + operation + ".json"
        )

    let frame =
        RemoteFixture.encode
            "recovery.export"
            {|
                operationId = operation
                requestSha256 = digest
                destination = destination
            |}

    let result = RemoteFixture.interactiveSessionAs "synthetic-steward" [ frame ]
    Expect.equal result.ExitCode 0 "Exporter-only native session completes"
    Expect.equal result.StandardError.Length 0 "Private export diagnostics stay private"
    use reply = JsonDocument.Parse(ReadOnlyMemory result.StandardOutput)

    Expect.equal
        (reply.RootElement.GetProperty("kind").GetString())
        "exported"
        "Known-ID export requires no inspection grant"

    use artifact = JsonDocument.Parse(File.ReadAllBytes(destination))

    Expect.equal
        (artifact.RootElement.GetProperty("operationId").GetString())
        operation
        "Downloaded envelope has the exact original operation"

    let preview = outcome "recovery.importEnvelopePreview" {| source = destination |}

    Expect.equal
        (preview.GetProperty("tag").GetString())
        "SUCCEEDED"
        "Independent authorized preview admits the signed artifact"

    Expect.equal
        (preview
            .GetProperty("data")
            .GetProperty("decodedEffect")
            .GetProperty("requestSha256")
            .GetString())
        digest
        "Signed preview preserves the exact canonical request"

    inspectRefusal operation
