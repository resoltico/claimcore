namespace ClaimCore.Witness

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Npgsql
open NpgsqlTypes

type Phase =
    | Intent
    | SettledAccepted
    | SettledRevoked
    | SettledAuthority
    | AbortedBeforeCommit
    | KeyRotated

type ScopeKind =
    | Installation
    | Case

type Ticket =
    {
        Sequence: int64
        Epoch: int64
        KeyId: Guid
        EntryHash: byte array
        PayloadHash: byte array
        OperationId: Guid
        Phase: Phase
        ScopeKind: ScopeKind
        SubjectCaseId: Guid option
    }

type Evidence =
    {
        Ticket: Ticket
        EncryptedPayload: byte array
    }

type JournalRecord =
    {
        Evidence: Evidence
        PreviousHash: byte array
    }

[<NoEquality; NoComparison>]
type Snapshot =
    {
        Identity: Identity
        Use: InstallationUseState
        InitialKeyId: Guid
        ActiveKeyId: Guid
        WriterGeneration: int64
        HandoffPending: bool
        ActivationPending: bool
        ActivationEventId: Guid option
        ActivationSequence: int64 option
        ActivationHash: byte array option
        TipSequence: int64
        TipHash: byte array
        LastAbortedHandoffId: Guid option
        LastAbortedHandoffSequence: int64 option
        LastAbortedHandoffHash: byte array option
    }

type JournalPage =
    {
        Items: JournalRecord list
        NextAfter: int64 option
    }

type MetadataRecord =
    {
        Ticket: Ticket
        PreviousHash: byte array
        PayloadPresent: bool
    }

type MetadataPage =
    {
        Items: MetadataRecord list
        NextAfter: int64 option
    }

type SubjectOperation = { OperationId: Guid; Intent: Ticket }

type SubjectScanResult =
    {
        CutoffSequence: int64
        CutoffHash: byte array
        IntentCount: int64
    }

[<NoEquality; NoComparison>]
type internal WriterHandoffEvidence =
    {
        HandoffId: Guid
        OldGeneration: int64
        NewGeneration: int64
        PreviousSequence: int64
        PreviousHash: byte array
        NewCapabilitySha256: byte array
        CheckpointSigningKeyId: Guid
        PrepareCanonical: byte array
        PrepareSignature: byte array
        ApprovalOneId: Guid
        ApprovalTwoId: Guid
        PrepareCandidateSha256: byte array
        PrepareSequence: int64
        PrepareHash: byte array
        PrepareRecordedAt: DateTimeOffset
        SettlementCandidateSha256: byte array option
        SettlementSequence: int64 option
        SettlementHash: byte array option
        SettlementCanonical: byte array option
        SettlementSignature: byte array option
        AbortCandidateSha256: byte array option
        AbortSequence: int64 option
        AbortHash: byte array option
        AbortCanonical: byte array option
        AbortSignatureOne: byte array option
        AbortSignatureTwo: byte array option
        AbortSigningKeyOne: Guid option
        AbortSigningKeyTwo: Guid option
        AbortRecordedAt: DateTimeOffset option
    }

module internal Encoding =
    let scope =
        function
        | Installation -> "INSTALLATION"
        | Case -> "CASE"

    let parseScope =
        function
        | "INSTALLATION" -> Installation
        | "CASE" -> Case
        | _ -> invalidOp "Witness scope is unknown."

    let phase =
        function
        | Intent -> "INTENT"
        | SettledAccepted -> "SETTLED_ACCEPTED"
        | SettledRevoked -> "SETTLED_REVOKED"
        | SettledAuthority -> "SETTLED_AUTHORITY"
        | AbortedBeforeCommit -> "ABORTED_BEFORE_COMMIT"
        | KeyRotated -> "KEY_ROTATED"

    let parsePhase =
        function
        | "INTENT" -> Intent
        | "SETTLED_ACCEPTED" -> SettledAccepted
        | "SETTLED_REVOKED" -> SettledRevoked
        | "SETTLED_AUTHORITY" -> SettledAuthority
        | "ABORTED_BEFORE_COMMIT" -> AbortedBeforeCommit
        | "KEY_ROTATED" -> KeyRotated
        | _ -> invalidOp "Witness phase is unknown."

module Baseline =
    let private resource name =
        let assembly = typeof<Identity>.Assembly

        use stream =
            assembly.GetManifestResourceStream(name)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Witness baseline resource is missing.")

        use buffer = new MemoryStream()
        stream.CopyTo(buffer)
        buffer.ToArray()

    let script () =
        let sql = resource "ClaimCore.Witness.Baseline.sql"
        let marker = resource "ClaimCore.Witness.Baseline.json"
        use document = JsonDocument.Parse(marker)
        let root = document.RootElement
        let digest = SHA256.HashData(sql) |> Convert.ToHexStringLower

        if
            root.GetProperty("baselineId").GetString() <> "claimcore-witness-v1"
            || root.GetProperty("sha256").GetString() <> digest
        then
            invalidOp "Witness baseline digest mismatch."

        UTF8Encoding(false, true).GetString(sql), digest

    let catalogScript () =
        resource "ClaimCore.Witness.Catalog.sql" |> UTF8Encoding(false, true).GetString

    let catalogDigest () =
        use document = JsonDocument.Parse(resource "ClaimCore.Witness.Catalog.json")

        if document.RootElement.GetProperty("serverVersion").GetString() <> "18.6" then
            invalidOp "Witness catalog server build differs."

        document.RootElement.GetProperty("sha256").GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Witness catalog digest is missing.")

    let private bindMarker
        (marker: NpgsqlCommand)
        (identity: Identity)
        scope
        keyId
        (keyCheckEnvelope: byte array)
        (writerCapability: byte array)
        digest
        =
        marker.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        marker.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
        |> ignore

        marker.Parameters.AddWithValue("keyId", NpgsqlDbType.Uuid, keyId) |> ignore

        marker.Parameters.AddWithValue("keyCheck", NpgsqlDbType.Bytea, keyCheckEnvelope)
        |> ignore

        marker.Parameters.AddWithValue("epoch", NpgsqlDbType.Bigint, identity.Epoch)
        |> ignore

        marker.Parameters.AddWithValue(
            "dataUseScope",
            NpgsqlDbType.Text,
            InstallationUse.scopeToken scope
        )
        |> ignore

        marker.Parameters.AddWithValue(
            "dataUsePhase",
            NpgsqlDbType.Text,
            (if scope = InstallationUseScope.SyntheticOnly then
                 "ACTIVE"
             else
                 "BOOTSTRAP_NO_CASES")
        )
        |> ignore

        marker.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, Array.zeroCreate<byte> 32)
        |> ignore

        marker.Parameters.AddWithValue(
            "writerHash",
            NpgsqlDbType.Bytea,
            SHA256.HashData(writerCapability)
        )
        |> ignore

        marker.Parameters.AddWithValue("id", NpgsqlDbType.Text, "claimcore-witness-v1")
        |> ignore

        marker.Parameters.AddWithValue("digest", NpgsqlDbType.Text, digest) |> ignore

    /// Run only with a separately held witness schema-owner credential on a fresh database.
    let initialize
        (ownerConnection: string)
        (identity: Identity)
        (scope: InstallationUseScope)
        (keyId: Guid)
        (keyCheckEnvelope: byte array)
        (writerCapability: byte array)
        =
        if
            keyId = Guid.Empty
            || keyCheckEnvelope.Length < 32
            || isNull (box writerCapability)
            || writerCapability.Length <> 32
            || not (writerCapability |> Array.exists ((<>) 0uy))
        then
            invalidArg (nameof keyCheckEnvelope) "Witness key check is invalid."

        let sql, digest = script ()
        use connection = PostgresTransport.connection ownerConnection
        connection.Open()
        use transaction = connection.BeginTransaction()
        use create = new NpgsqlCommand(sql, connection, transaction)
        create.ExecuteNonQuery() |> ignore

        use marker =
            new NpgsqlCommand(
                "INSERT INTO claimcore_witness.installation "
                + "(installation_id,lineage_id,initial_key_id,active_key_id,key_check_envelope,"
                + "epoch,data_use_scope,data_use_phase,tip_hash,writer_capability_sha256,baseline_id,baseline_sha256) "
                + "VALUES (@installation,@lineage,@keyId,@keyId,@keyCheck,@epoch,@dataUseScope,@dataUsePhase,"
                + "@hash,@writerHash,@id,@digest)",
                connection,
                transaction
            )

        bindMarker marker identity scope keyId keyCheckEnvelope writerCapability digest
        marker.ExecuteNonQuery() |> ignore
        transaction.Commit()
