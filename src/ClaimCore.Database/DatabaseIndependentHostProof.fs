namespace ClaimCore.Database

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Postgres

[<NoEquality; NoComparison>]
type internal VerifiedIndependentHostProof =
    {
        Scope: string
        ExpectedProbeSha256: string
        ProbeEvidenceSha256: string
        RealDataReady: bool
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        W1Sequence: int64
        W1Hash: string
        PublicationManifestSha256: string
        TopologyManifestSha256: string
        ValidUntil: DateTimeOffset
    }

[<NoEquality; NoComparison>]
type internal HistoricalIndependentHostDigest =
    {
        AggregateSha256: string
        Scope: string
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        W1Sequence: int64
        W1Hash: string
        PublicationManifestSha256: string
        TopologyManifestSha256: string
        ExpectedProbeSha256: string
        ProbeEvidenceSha256: string
        SignedAt: DateTimeOffset
    }

/// The generic OSS build has no independent trust root; absence is a definite refusal.
/// A deployment-specific reviewed build may pin one, after which raw signed host evidence
/// must be rechecked before this issuer can return a typed proof.
module internal DatabaseIndependentHostProof =
    let private signedClaims (signed: SignedFencedTailEvidence) at =
        let backup =
            DatabaseRestoreReportClaims.parse signed.Report at
            |> Option.defaultWith (fun () -> invalidOp "Independent host report is invalid.")

        let fence =
            DatabaseRestoreWriterFenceClaims.parse signed.Fence at
            |> Option.defaultWith (fun () -> invalidOp "Independent host writer fence is invalid.")

        let tail =
            DatabaseRestoreFencedTailClaims.parse signed.Supplement at
            |> Option.defaultWith (fun () -> invalidOp "Independent host final tail is invalid.")

        backup, fence, tail

    let private sourceBound
        (backup: RestoreReportClaims)
        (fence: WriterFenceClaims)
        (tail: FencedTailClaims)
        (publication: TrustedRestorePublication)
        (aggregate: IndependentHostAggregate)
        reportSha
        fenceSha
        =
        backup.SignerKeyId = publication.ReportSignerKeyId
        && tail.CheckpointSignerKeyId = publication.CheckpointSignerKeyId
        && tail.HandoffId <> Guid.Empty
        && tail.W1Sequence > publication.WitnessCutoff
        && tail.ReportSha256 = reportSha
        && tail.FenceReportSha256 = fenceSha
        && fence.IndependentProbeSha256 <> String.replicate 64 "0"
        && tail.ValidUntil >= aggregate.CheckedAt
        && fence.ValidUntil >= aggregate.CheckedAt

    let private verified
        (rootKey: byte array)
        (documents: IndependentHostDocuments)
        (signed: SignedFencedTailEvidence)
        (publication: TrustedRestorePublication)
        (now: DateTimeOffset option)
        =
        let signedAt = DatabaseIndependentHostAggregate.checkedAt documents.Aggregate
        let at = now |> Option.defaultValue signedAt

        let backup, fence, tail = signedClaims signed at

        let topology =
            DatabaseIndependentHostTopology.verifyWithRoot
                rootKey
                publication
                tail
                documents.Topology
                documents.TopologySignature
                at
            |> Option.defaultWith (fun () -> invalidOp "Independent host topology is untrusted.")

        let reportSha = DatabaseIndependentHostJson.sha256 signed.Report
        let fenceSha = DatabaseIndependentHostJson.sha256 signed.Fence
        let supplementSha = DatabaseIndependentHostJson.sha256 signed.Supplement

        let aggregate =
            DatabaseIndependentHostAggregate.verify
                documents
                topology
                publication
                backup
                fence
                tail
                reportSha
                fenceSha
                supplementSha
                now

        if not (sourceBound backup fence tail publication aggregate reportSha fenceSha) then
            invalidOp "Independent host source evidence diverged."

        tail, fence, topology, aggregate

    let verifyWithRoot
        (rootKey: byte array)
        (documents: IndependentHostDocuments)
        (signed: SignedFencedTailEvidence)
        (publication: TrustedRestorePublication)
        (dbNow: DateTimeOffset)
        : VerifiedIndependentHostProof option =
        try
            let tail, fence, topology, aggregate =
                verified rootKey documents signed publication (Some dbNow)

            let expiry =
                [ tail.ValidUntil; fence.ValidUntil; topology.ValidUntil; aggregate.ValidUntil ]
                |> List.min

            if dbNow >= expiry then
                None
            else
                Some
                    {
                        Scope = "full"
                        ExpectedProbeSha256 = fence.IndependentProbeSha256
                        ProbeEvidenceSha256 = aggregate.ProbeEvidenceSha256
                        RealDataReady = true
                        InstallationId = tail.InstallationId
                        LineageId = tail.LineageId
                        Epoch = tail.Epoch
                        WriterGeneration = tail.NewGeneration
                        W1Sequence = tail.W1Sequence
                        W1Hash = tail.W1Hash
                        PublicationManifestSha256 = publication.ManifestSha256
                        TopologyManifestSha256 = topology.Digest
                        ValidUntil = expiry
                    }
        with _ ->
            None

    let historicalWithRoot
        (rootKey: byte array)
        (documents: IndependentHostDocuments)
        (signed: SignedFencedTailEvidence)
        (publication: TrustedRestorePublication)
        : HistoricalIndependentHostDigest option =
        try
            let tail, fence, topology, aggregate =
                verified rootKey documents signed publication None

            Some
                {
                    AggregateSha256 = aggregate.Digest
                    Scope = "full"
                    InstallationId = tail.InstallationId
                    LineageId = tail.LineageId
                    Epoch = tail.Epoch
                    WriterGeneration = tail.NewGeneration
                    W1Sequence = tail.W1Sequence
                    W1Hash = tail.W1Hash
                    PublicationManifestSha256 = publication.ManifestSha256
                    TopologyManifestSha256 = topology.Digest
                    ExpectedProbeSha256 = fence.IndependentProbeSha256
                    ProbeEvidenceSha256 = aggregate.ProbeEvidenceSha256
                    SignedAt = aggregate.CheckedAt
                }
        with _ ->
            None

    let private withRoots (roots: byte array list) action =
        if List.isEmpty roots then
            None
        else
            try
                try
                    let documents = DatabaseIndependentHostEvidence.load ()

                    try
                        roots |> List.tryPick (fun root -> action root documents)
                    finally
                        DatabaseIndependentHostEvidence.dispose documents
                with _ ->
                    None
            finally
                roots
                |> List.iter (fun root -> CryptographicOperations.ZeroMemory(root.AsSpan()))

    let current
        (owner: NpgsqlConnection)
        (signed: SignedFencedTailEvidence)
        (publication: TrustedRestorePublication)
        (dbNow: DateTimeOffset)
        : VerifiedIndependentHostProof option =
        try
            OwnerConnection.requireIdentity owner

            withRoots
                (DatabaseRestorePublication.reviewedRootKey () |> Option.toList)
                (fun root documents -> verifyWithRoot root documents signed publication dbNow)
        with _ ->
            None

    let historicalDigest
        (owner: NpgsqlConnection)
        (signed: SignedFencedTailEvidence)
        (publication: TrustedRestorePublication)
        (_dbNow: DateTimeOffset)
        : HistoricalIndependentHostDigest option =
        try
            OwnerConnection.requireIdentity owner

            withRoots
                (DatabaseRestorePublication.reviewedRootKey () |> Option.toList)
                (fun root documents -> historicalWithRoot root documents signed publication)
        with _ ->
            None
