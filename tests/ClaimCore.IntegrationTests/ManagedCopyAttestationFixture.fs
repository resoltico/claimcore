module internal ClaimCore.IntegrationTests.ManagedCopyAttestationFixture

open System
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Npgsql
open ClaimCore.Witness

let private utc (value: DateTimeOffset) =
    value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)

let private postgresControl owner =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT s.system_identifier::text,c.timeline_id,i.bytes_per_wal_segment "
            + "FROM pg_control_system() s CROSS JOIN pg_control_checkpoint() c "
            + "CROSS JOIN pg_control_init() i",
            connection
        )

    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        invalidOp "Synthetic PostgreSQL control values are absent."

    reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)

let registerBase owner (tip: Snapshot) (signingKeyId: Guid) (eventId: Guid) (copyId: Guid) =
    let systemId, timeline, segmentBytes = postgresControl owner
    let captured = DateTimeOffset.UtcNow
    let values = SortedDictionary<string, JsonElement>(StringComparer.Ordinal)

    let put name value =
        values.Add(name, JsonSerializer.SerializeToElement(value))

    let putNull name =
        use document = JsonDocument.Parse("null")
        values.Add(name, document.RootElement.Clone())

    put "backupManifestSha256" (Convert.ToHexStringLower(SHA256.HashData([| 1uy |])))
    put "capturedAt" (utc captured)
    put "ciphertextBytes" 128L
    put "ciphertextSha256" (Convert.ToHexStringLower(SHA256.HashData([| 2uy |])))
    put "cluster" "primary"
    put "copyId" (copyId.ToString("D"))
    put "copyRevision" 1
    put "custodianCommitment" (Convert.ToHexStringLower(SHA256.HashData([| 3uy |])))
    put "cycleId" (Guid.NewGuid().ToString("D"))
    putNull "deletionProofSha256"
    put "encryptionKeyId" (Guid.NewGuid().ToString("D"))
    put "epoch" tip.Identity.Epoch
    put "eventId" (eventId.ToString("D"))
    put "eventKind" "REGISTER"
    put "format" "claimcore-managed-copy-attestation-1"
    put "installationId" (tip.Identity.InstallationId.ToString("D"))
    put "kind" "BASE"
    putNull "lastVerifiedAt"
    put "lineageId" (tip.Identity.LineageId.ToString("D"))
    put "locationCommitment" (Convert.ToHexStringLower(SHA256.HashData([| 4uy |])))
    put "postgresSystemId" systemId
    putNull "previousEventHash"
    put "primaryRegistration" "NOT_REGISTERED"
    put "retainUntil" (utc (captured.AddDays(7.0)))
    put "signingKeyId" (signingKeyId.ToString("D"))
    putNull "sourceCaseId"
    put "state" "UNVERIFIED"
    put "timeline" timeline
    putNull "verificationProofSha256"
    put "walEndLsn" "0/2"
    putNull "walSegment"
    put "walSegmentBytes" segmentBytes
    put "walStartLsn" "0/1"
    put "witnessCutoffHash" (Convert.ToHexStringLower(tip.TipHash))
    put "witnessCutoffSequence" tip.TipSequence
    (JsonSerializer.Serialize(values) + "\n") |> Encoding.ASCII.GetBytes

let registerVariant owner tip signingKeyId eventId copyId kind cluster sourceCaseId =
    use source = JsonDocument.Parse(registerBase owner tip signingKeyId eventId copyId)
    let values = SortedDictionary<string, JsonElement>(StringComparer.Ordinal)

    for property in source.RootElement.EnumerateObject() do
        values[property.Name] <- property.Value.Clone()

    let put name value =
        values[name] <- JsonSerializer.SerializeToElement(value)

    use nullValue = JsonDocument.Parse("null")

    let putNull name =
        values[name] <- nullValue.RootElement.Clone()

    put "kind" kind
    put "cluster" cluster

    match sourceCaseId with
    | Some(caseId: Guid) -> put "sourceCaseId" (caseId.ToString("D"))
    | None -> putNull "sourceCaseId"

    for name in [ "backupManifestSha256"; "walStartLsn"; "walEndLsn"; "walSegment"; "cycleId" ] do
        putNull name

    if kind <> "SNAPSHOT" && kind <> "REPLICA" then
        for name in [ "postgresSystemId"; "timeline"; "walSegmentBytes" ] do
            putNull name

    (JsonSerializer.Serialize(values) + "\n") |> Encoding.ASCII.GetBytes

let transitionFromRegister
    (registration: byte array)
    (eventId: Guid)
    revision
    eventKind
    state
    (previousHash: byte array)
    (actionTip: Snapshot)
    =
    use source = JsonDocument.Parse(registration)
    let values = SortedDictionary<string, JsonElement>(StringComparer.Ordinal)

    for property in source.RootElement.EnumerateObject() do
        values[property.Name] <- property.Value.Clone()

    let put name value =
        values[name] <- JsonSerializer.SerializeToElement(value)

    let putNull name =
        use document = JsonDocument.Parse("null")
        values[name] <- document.RootElement.Clone()

    put "eventId" (eventId.ToString("D"))
    put "copyRevision" revision
    put "eventKind" eventKind
    put "state" state
    put "previousEventHash" (Convert.ToHexStringLower(previousHash))
    put "actionWitnessCutoffSequence" actionTip.TipSequence
    put "actionWitnessCutoffHash" (Convert.ToHexStringLower(actionTip.TipHash))
    put "format" "claimcore-managed-copy-transition-1"
    putNull "deletionApprovalId"
    put "primaryRegistration" "REGISTERED"
    (JsonSerializer.Serialize(values) + "\n") |> Encoding.ASCII.GetBytes
