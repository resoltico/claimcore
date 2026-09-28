module internal ClaimCore.IntegrationTests.IndependentHostProbeFixture

open System
open System.Buffers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open NSec.Cryptography
open ClaimCore.Database

let private hash (bytes: byte array) =
    SHA256.HashData(bytes) |> Convert.ToHexStringLower

let private s (value: string) : JsonNode | null = JsonValue.Create(value)
let private n (value: int64) : JsonNode | null = JsonValue.Create(value)
let private b (value: bool) : JsonNode | null = JsonValue.Create(value)

let private objectOf (fields: (string * (JsonNode | null)) list) =
    let output = JsonObject()

    for name, value in fields do
        output[name] <- value

    output

let rec private write (writer: Utf8JsonWriter) (value: JsonElement) =
    match value.ValueKind with
    | JsonValueKind.Object ->
        writer.WriteStartObject()

        for item in value.EnumerateObject() |> Seq.sortBy _.Name do
            writer.WritePropertyName(item.Name)
            write writer item.Value

        writer.WriteEndObject()
    | JsonValueKind.Array ->
        writer.WriteStartArray()

        for item in value.EnumerateArray() do
            write writer item

        writer.WriteEndArray()
    | _ -> value.WriteTo(writer)

let canonical (value: JsonNode) =
    use parsed = JsonDocument.Parse(value.ToJsonString())
    let buffer = ArrayBufferWriter<byte>()
    use writer = new Utf8JsonWriter(buffer)
    write writer parsed.RootElement
    writer.Flush()
    Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]

let private pem (key: Key) =
    let prefix = Convert.FromHexString("302a300506032b6570032100")
    let der = Array.append prefix (key.PublicKey.Export(KeyBlobFormat.RawPublicKey))

    Encoding.ASCII.GetBytes(
        "-----BEGIN PUBLIC KEY-----\n"
        + Convert.ToBase64String(der)
        + "\n-----END PUBLIC KEY-----\n"
    )

let private walObject cluster =
    {
        ObjectId = Guid.NewGuid()
        Cluster = cluster
        RelativePath = "final/" + cluster.ToLowerInvariant() + "/segment.age"
        CiphertextSha256 = String.replicate 64 (if cluster = "PRIMARY" then "a" else "b")
        CiphertextBytes = 16777248L
        Segment = "000000010000000000000001"
        SegmentBytes = 16777216
    }

let private walJson (value: FencedWalObject) =
    objectOf
        [
            "objectId", s (value.ObjectId.ToString("D"))
            "cluster", s value.Cluster
            "relativePath", s value.RelativePath
            "ciphertextSha256", s value.CiphertextSha256
            "ciphertextBytes", n value.CiphertextBytes
            "walSegment", s value.Segment
            "walSegmentBytes", n (int64 value.SegmentBytes)
        ]

[<NoEquality; NoComparison>]
type ArchiveProbeFixture =
    {
        Key: Key
        PublicPem: byte array
        Pin: IndependentRolePin
        Backup: RestoreReportClaims
        Tail: FencedTailClaims
        Entry: JsonDocument
        Now: DateTimeOffset
        Nonce: string
        SupplementSha: string
    }

    interface IDisposable with
        member this.Dispose() =
            this.Entry.Dispose()
            this.Key.Dispose()

let private backup installation lineage now (archiveCopy: RestoreCustodyObject) =
    {
        Scope = "full"
        RealDataReady = false
        InstallationId = installation
        LineageId = lineage
        Epoch = 1L
        CycleId = Guid.NewGuid()
        BackupCaptureSequence = 1L
        BackupCaptureHash = String.replicate 64 "1"
        WitnessCutoff = 2L
        WitnessCutoffHash = String.replicate 64 "2"
        PrimarySystemId = "111"
        PrimaryTimeline = 1L
        WitnessSystemId = "222"
        WitnessTimeline = 1L
        PrimaryRegisteredWalHorizon = "0/1000000"
        WitnessRegisteredWalHorizon = "0/1000000"
        SignerKeyId = Guid.NewGuid()
        VerifierBinarySha256 = String.replicate 64 "3"
        EvidenceIndexSha256 = String.replicate 64 "4"
        CheckpointSha256 = String.replicate 64 "5"
        SignedInventoryFileSha256 = String.replicate 64 "6"
        QuiescentBarrierSha256 = String.replicate 64 "7"
        CatalogManifestSha256 = String.replicate 64 "8"
        AuthorityRevision = 1L
        AuthorizedApprovers = []
        ArchiveCustody = archiveCopy
        CheckpointCustody =
            { archiveCopy with
                ObjectId = Guid.NewGuid()
            }
        CustodyKeyId = Guid.NewGuid()
        CustodyPublicKeySha256 = String.replicate 64 "9"
        CheckedAt = now
        ValidUntil = now.AddMinutes(10.)
    }

let private tail installation lineage now objects =
    {
        Scope = "full"
        InstallationId = installation
        LineageId = lineage
        Epoch = 1L
        OldGeneration = 1L
        NewGeneration = 2L
        HandoffId = Guid.NewGuid()
        W1Sequence = 4L
        W1Hash = String.replicate 64 "c"
        ReportSha256 = String.replicate 64 "d"
        FenceReportSha256 = String.replicate 64 "e"
        CheckpointSignerKeyId = Guid.NewGuid()
        PrimaryRegisteredWalHorizon = "0/1000000"
        WitnessRegisteredWalHorizon = "0/1000000"
        PrimaryFinalWalEndpoint = "0/2000000"
        WitnessFinalWalEndpoint = "0/2000000"
        ArchiveRoot = "/synthetic/private/archive"
        WalObjects = objects
        CheckedAt = now
        ValidUntil = now.AddMinutes(10.)
    }

let private archiveReport
    (installation: Guid)
    (lineage: Guid)
    (copy: RestoreCustodyObject)
    (pin: IndependentRolePin)
    (objects: FencedWalObject list)
    includeAll
    nonce
    supplement
    issued
    expires
    =
    let final = JsonArray()

    for item in (if includeAll then objects else objects |> List.take 1) do
        final.Add(walJson item)

    objectOf
        [
            "format", s "claimcore-deployment-probe-1"
            "role", s "archive"
            "nonce", s nonce
            "qualificationSha256", s supplement
            "issuedAt", s issued
            "expiresAt", s expires
            "machineHash", s pin.MachineHash
            "storageHash", s pin.StorageHash
            "adminActorId", s (pin.AdminActorId.ToString("D"))
            "hostKeyId", s (pin.HostKeyId.ToString("D"))
            "containerized", b false
            "availabilityKind", s "retained-copy"
            "available", b true
            "keyChallengeProofSha256", null
            "installationId", s (installation.ToString("D"))
            "lineageId", s (lineage.ToString("D"))
            "epoch", n 1L
            "objectId", s (copy.ObjectId.ToString("D"))
            "objectSha256", s copy.Sha256
            "objectBytes", n copy.Bytes
            "finalWalObjects", final
            "finalWalObjectCount", n (if includeAll then 2L else 1L)
            "finalWalObjectSha256", s (DatabaseRestoreWalObjectDigest.compute objects)
        ]

let private archiveEntry (key: Key) (pin: IndependentRolePin) issued expires report =
    let body = canonical report
    let signature = SignatureAlgorithm.Ed25519.Sign(key, body)

    let entry =
        objectOf
            [
                "role", s "archive"
                "canonicalBase64", s (Convert.ToBase64String body)
                "signatureBase64", s (Convert.ToBase64String signature)
                "probeSha256", s (hash body)
                "machineHash", s pin.MachineHash
                "storageHash", s pin.StorageHash
                "adminActorId", s (pin.AdminActorId.ToString("D"))
                "hostKeyId", s (pin.HostKeyId.ToString("D"))
                "checkedAt", s issued
                "validUntil", s expires
            ]

    JsonDocument.Parse(canonical entry)

let private archivePin publicPem =
    {
        Role = "archive"
        PublicKeySha256 = hash publicPem
        MachineHash = String.replicate 64 "1"
        StorageHash = String.replicate 64 "2"
        AdminActorId = Guid.NewGuid()
        HostKeyId = Guid.NewGuid()
        SshHostKeySha256 = String.replicate 64 "3"
    }

let createArchiveProbeWithFinalObjects now includeAll : ArchiveProbeFixture =
    let key = Key.Create(SignatureAlgorithm.Ed25519)
    let publicPem = pem key
    let installation = Guid.NewGuid()
    let lineage = Guid.NewGuid()
    let objects = [ walObject "PRIMARY"; walObject "WITNESS" ]

    let copy =
        {
            ObjectId = Guid.NewGuid()
            Sha256 = String.replicate 64 "f"
            Bytes = 128L
        }

    let backup = backup installation lineage now copy
    let tail = tail installation lineage now objects
    let nonce = String.replicate 64 "0"
    let supplement = String.replicate 64 "a"
    let issued = now.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
    let expires = now.AddSeconds(60.).ToString("yyyy-MM-ddTHH:mm:ss'Z'")

    let pin = archivePin publicPem

    let report =
        archiveReport
            installation
            lineage
            copy
            pin
            objects
            includeAll
            nonce
            supplement
            issued
            expires

    {
        Key = key
        PublicPem = publicPem
        Pin = pin
        Backup = backup
        Tail = tail
        Entry = archiveEntry key pin issued expires report
        Now = now
        Nonce = nonce
        SupplementSha = supplement
    }

let createArchiveProbe now =
    createArchiveProbeWithFinalObjects now true
