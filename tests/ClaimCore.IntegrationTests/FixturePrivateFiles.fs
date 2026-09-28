module internal ClaimCore.IntegrationTests.FixturePrivateFiles

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Witness
open ClaimCore.IntegrationTests.FixtureEnvironment

let private suppressionKey = RandomNumberGenerator.GetBytes(32)
let private suppressionKeyId = Guid.NewGuid()

let private commitment (installationId: Guid) (lineageId: Guid) label (value: byte array) =
    let prefix =
        Encoding.UTF8.GetBytes(
            "claimcore:suppression:v1:"
            + installationId.ToString("D")
            + ":"
            + lineageId.ToString("D")
            + ":"
            + suppressionKeyId.ToString("D")
            + ":"
            + label
            + ":"
        )

    HMACSHA256.HashData(suppressionKey, Array.concat [ prefix; value ])

let syntheticSuppressionCheck (installationId: Guid) (lineageId: Guid) =
    suppressionKeyId, commitment installationId lineageId "key-check" Array.empty

let syntheticCommitments (identity: Identity) : ISuppressionCommitments =
    { new ISuppressionCommitments with
        member _.InstallationId = identity.InstallationId
        member _.LineageId = identity.LineageId
        member _.KeyId = suppressionKeyId
        member _.Admit() = ()

        member _.Reference(reference) =
            commitment
                identity.InstallationId
                identity.LineageId
                "reference"
                (Encoding.UTF8.GetBytes(reference))

        member _.Operation(operationId) =
            commitment
                identity.InstallationId
                identity.LineageId
                "operation"
                (Encoding.UTF8.GetBytes(operationId.ToString("D")))

        member _.RequestCandidate(canonical) =
            commitment identity.InstallationId identity.LineageId "request-candidate" canonical

        member _.PurgeProposal(canonicalDraft) =
            commitment identity.InstallationId identity.LineageId "purge-proposal" canonicalDraft

        member _.ApprovalDraft(canonicalDraft) =
            commitment identity.InstallationId identity.LineageId "approval-draft" canonicalDraft

        member _.ApprovalCanonical(canonicalApproval) =
            commitment
                identity.InstallationId
                identity.LineageId
                "approval-canonical"
                canonicalApproval
    }

let canonicalRoot () =
    let root = Path.GetTempPath()

    if OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal) then
        "/private" + root
    else
        root

let create suffix application writerCapability =
    let directory = Path.Combine(canonicalRoot (), "claimcore-integration-" + suffix)
    let connectionFile = Path.Combine(directory, "application.connection")
    privateFile directory connectionFile application

    let suppressionFile = Path.Combine(directory, "suppression.key")

    let suppressionJson =
        JsonSerializer.Serialize(
            {|
                version = 1
                keyId = suppressionKeyId
                materialBase64 = Convert.ToBase64String(suppressionKey)
            |}
        )

    privateFile directory suppressionFile suppressionJson

    let artifactFile = Path.Combine(directory, "recovery-artifact-keys.json")
    let encryption = RandomNumberGenerator.GetBytes(32)
    let mac = RandomNumberGenerator.GetBytes(32)

    try
        let id = Guid.NewGuid()

        let artifactJson =
            JsonSerializer.Serialize(
                {|
                    version = 1
                    activeKeyId = id
                    artifactLifetimeSeconds = 3600
                    keys =
                        [|
                            {|
                                id = id
                                encryptionBase64 = Convert.ToBase64String(encryption)
                                macBase64 = Convert.ToBase64String(mac)
                                issueFrom = DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
                                issueUntil = DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)
                                verifyUntil = DateTimeOffset(2030, 2, 1, 0, 0, 0, TimeSpan.Zero)
                                maximumExports = 65536
                            |}
                        |]
                |}
            )

        privateFile directory artifactFile artifactJson
    finally
        CryptographicOperations.ZeroMemory(encryption)
        CryptographicOperations.ZeroMemory(mac)

    let capabilityFile = Path.Combine(directory, "witness-writer.capability")
    privateBytes directory capabilityFile writerCapability
    directory, connectionFile, suppressionFile, artifactFile, capabilityFile
