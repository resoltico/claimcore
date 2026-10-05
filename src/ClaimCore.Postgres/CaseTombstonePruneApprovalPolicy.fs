namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application

module internal CaseTombstonePruneApprovalPolicy =
    let digest (text: string) =
        try
            let bytes: byte array = Convert.FromHexString(text)

            if bytes.Length = 32 && text = Convert.ToHexStringLower bytes then
                Some bytes
            else
                None
        with _ ->
            None

    let valid
        (value: TombstonePruneProposal)
        (approvalId: Guid)
        (expiresAt: DateTimeOffset)
        (instant: DateTimeOffset)
        =
        value.EventId <> Guid.Empty
        && value.CaseId <> Guid.Empty
        && value.PurgeEventId <> Guid.Empty
        && approvalId <> Guid.Empty
        && value.PurgeWitnessSequence > 0L
        && value.PurgeWitnessEpoch > 0L
        && value.CutoffSequence >= value.PurgeWitnessSequence
        && value.TargetCount > 0L
        && value.ExpectedAuthorityRevision >= 0L
        && (digest value.PurgeWitnessHash |> Option.isSome)
        && (digest value.CutoffHash |> Option.isSome)
        && (digest value.TargetDigest |> Option.isSome)
        && (digest value.ExpectedAuthorityHash |> Option.isSome)
        && instant.Offset = TimeSpan.Zero
        && value.ValidUntil.Offset = TimeSpan.Zero
        && expiresAt.Offset = TimeSpan.Zero
        && value.ValidUntil.UtcTicks % 10L = 0L
        && expiresAt.UtcTicks % 10L = 0L
        && value.ValidUntil > instant
        && value.ValidUntil <= instant.AddHours(24.0)
        && expiresAt > instant
        && expiresAt <= value.ValidUntil

    let matches (stored: StoredCaseTombstone) (value: TombstonePruneProposal) =
        stored.PruneEventId.IsNone
        && stored.PurgeEventId = value.PurgeEventId
        && stored.PurgeWitnessSequence = value.PurgeWitnessSequence
        && stored.PurgeWitnessEpoch = value.PurgeWitnessEpoch
        && stored.PurgeWitnessHash =
            (digest value.PurgeWitnessHash |> Option.defaultValue Array.empty)
        && stored.AuthorityRevision = value.ExpectedAuthorityRevision
        && stored.AuthorityHash =
            (digest value.ExpectedAuthorityHash |> Option.defaultValue Array.empty)

    let targetMatches
        (witness: WitnessProtocol)
        (value: TombstonePruneProposal)
        (ct: CancellationToken)
        =
        task {
            let cutoffHash = digest value.CutoffHash |> Option.defaultValue Array.empty

            let! seal =
                CaseWitnessPayloadTargets.scan
                    witness
                    value.CaseId
                    value.CutoffSequence
                    cutoffHash
                    (fun _ -> Task.FromResult())
                    ct

            return
                seal.TargetCount = value.TargetCount
                && seal.TargetDigest =
                    (digest value.TargetDigest |> Option.defaultValue Array.empty)
        }
