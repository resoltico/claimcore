namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Postgres

/// Exact comparison between independently verified host evidence and local signed WAL facts.
module internal DatabaseFencedTailHostBinding =
    let private hash (value: byte array) = Convert.ToHexStringLower(value)

    let private currentClock (owner: NpgsqlConnection) =
        use command = new NpgsqlCommand("SELECT clock_timestamp()", owner)

        match command.ExecuteScalar() with
        | :? DateTimeOffset as value -> value.ToUniversalTime()
        | :? DateTime as value -> DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
        | _ -> invalidOp "Owner database clock is unavailable."

    let matchesLocal
        (host: VerifiedIndependentHostProof)
        (local: FencedTailVerification)
        (now: DateTimeOffset)
        =
        host.Scope = "full"
        && host.RealDataReady
        && host.InstallationId = local.InstallationId
        && host.LineageId = local.LineageId
        && host.Epoch = local.Epoch
        && host.WriterGeneration = local.WriterGeneration
        && host.W1Sequence = local.W1Sequence
        && host.W1Hash = local.W1Hash
        && host.PublicationManifestSha256 = local.PublicationManifestSha256
        && host.ExpectedProbeSha256 = local.IndependentProbeSha256
        && host.ProbeEvidenceSha256 = local.ProbeEvidenceSha256
        && now < host.ValidUntil
        && now < local.ValidUntil

    let private matchesCandidate
        (host: VerifiedIndependentHostProof)
        (candidate: WriterActivationEvidence)
        now
        =
        host.Scope = "full"
        && host.RealDataReady
        && host.InstallationId = candidate.InstallationId
        && host.LineageId = candidate.LineageId
        && host.Epoch = candidate.Epoch
        && host.WriterGeneration = candidate.WriterGeneration
        && host.W1Sequence = candidate.W1Sequence
        && host.W1Hash = hash candidate.W1Hash
        && host.PublicationManifestSha256 = hash candidate.PublicationManifestSha256
        && host.ExpectedProbeSha256 = hash candidate.IndependentProbeSha256
        && host.ProbeEvidenceSha256 = hash candidate.ProbeEvidenceSha256
        && now < host.ValidUntil
        && now < candidate.ValidUntil

    let private matchesLocalCandidate (local: FencedTailVerification) candidate =
        let expected = DatabaseFencedTailEvidenceMapping.activation local
        let left = WriterActivationCandidate.encode expected

        try
            let right = WriterActivationCandidate.encode candidate

            try
                CryptographicOperations.FixedTimeEquals(left, right)
            finally
                CryptographicOperations.ZeroMemory(right)
        finally
            CryptographicOperations.ZeroMemory(left)

    let private qualification
        (host: VerifiedIndependentHostProof)
        (candidate: WriterActivationEvidence)
        : WriterActivationQualification =
        {
            HandoffId = candidate.HandoffId
            InstallationId = candidate.InstallationId
            LineageId = candidate.LineageId
            Epoch = candidate.Epoch
            WriterGeneration = candidate.WriterGeneration
            W1Sequence = candidate.W1Sequence
            W1Hash = Array.copy candidate.W1Hash
            PublicationManifestSha256 = Array.copy candidate.PublicationManifestSha256
            ReportSha256 = Array.copy candidate.ReportSha256
            FenceSha256 = Array.copy candidate.FenceSha256
            SupplementSha256 = Array.copy candidate.SupplementSha256
            FinalWalObjectSha256 = Array.copy candidate.FinalWalObjectSha256
            FinalWalObjectCount = candidate.FinalWalObjectCount
            IndependentProbeSha256 = Array.copy candidate.IndependentProbeSha256
            ProbeEvidenceSha256 = Array.copy candidate.ProbeEvidenceSha256
            CheckpointSigningKeyId = candidate.CheckpointSigningKeyId
            CheckpointHolderActorId = candidate.CheckpointHolderActorId
            ValidUntil = min host.ValidUntil candidate.ValidUntil
        }

    let verifier
        (owner: NpgsqlConnection)
        (publication: TrustedRestorePublication)
        (local: FencedTailVerification)
        =
        { new IWriterActivationEvidenceVerifier with
            member _.Verify(candidate, cancellationToken: CancellationToken) =
                task {
                    try
                        cancellationToken.ThrowIfCancellationRequested()
                        let now = currentClock owner

                        let signed: SignedFencedTailEvidence =
                            {
                                Report = candidate.SignedReport
                                ReportSignature = candidate.ReportSignature
                                Fence = candidate.SignedFence
                                FenceSignature = candidate.FenceSignature
                                Supplement = candidate.SignedSupplement
                                SupplementSignature = candidate.SupplementSignature
                            }

                        match DatabaseIndependentHostProof.current owner signed publication now with
                        | Some host when
                            matchesCandidate host candidate now
                            && matchesLocalCandidate local candidate
                            ->
                            return Some(qualification host candidate)
                        | _ -> return None
                    with _ ->
                        return None
                }
        }
