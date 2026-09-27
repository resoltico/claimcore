namespace ClaimCore.ContractGeneration

open System
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain

module internal WebTombstoneCorpusSamples =
    let private eventId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")
    let private caseId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")

    let private review: TombstoneReview =
        {
            CaseId = caseId
            PurgeEventId = eventId
            PurgeWitnessSequence = 41L
            PurgeWitnessEpoch = 1L
            PurgeWitnessHash = String.replicate 64 "a"
            CutoffSequence = 42L
            CutoffHash = String.replicate 64 "b"
            TargetCount = 8L
            TargetDigest = String.replicate 64 "c"
            PrivacyPhase = PrivacyPhase.ErasurePending
            WitnessPayloadPruned = false
            ManagedCopyCertificationPending = true
            AuthorityRevision = 2L
            AuthorityHash = String.replicate 64 "d"
            ActiveHolds =
                [
                    {
                        HoldId = eventId
                        ReviewOn = DateOnly(2026, 10, 1)
                    }
                ]
            RequiredDistinctStewardApprovals = 2
        }

    let private sample id endpoint bytes =
        WebCorpusSamples.sample id endpoint bytes

    let private reviewSamples =
        [
            "available", TombstoneReviewOutcome.Available review
            "pruned-copy-pending",
            TombstoneReviewOutcome.Available
                { review with
                    WitnessPayloadPruned = true
                }
            "payload-erased-suppression-retained",
            TombstoneReviewOutcome.Available
                { review with
                    PrivacyPhase = PrivacyPhase.PayloadErasedSuppressionRetained
                    WitnessPayloadPruned = true
                    ManagedCopyCertificationPending = false
                    ActiveHolds = []
                }
            "erasure-final-suppression-evidence-retained",
            TombstoneReviewOutcome.Available
                { review with
                    PrivacyPhase = PrivacyPhase.ErasureFinal
                    WitnessPayloadPruned = true
                    ManagedCopyCertificationPending = false
                    ActiveHolds = []
                    RequiredDistinctStewardApprovals = 0
                }
            "unavailable", TombstoneReviewOutcome.ResourceUnavailable
            "cancelled", TombstoneReviewOutcome.Cancelled
            "failed", TombstoneReviewOutcome.Failed CoreFault.StoreUnavailable
        ]
        |> List.map (fun (suffix, outcome) ->
            sample
                ("tombstone-review-" + suffix)
                "tombstone.review"
                (WebWireCodec.tombstoneReview outcome))

    let private writeSamples endpoint =
        [
            "applied", TombstoneWriteOutcome.Applied(eventId, 3L)
            "refused", TombstoneWriteOutcome.Refused LifecycleRefusal.ApprovalRequired
            "hold", TombstoneWriteOutcome.Refused LifecycleRefusal.HoldActive
            "unavailable", TombstoneWriteOutcome.ResourceUnavailable
            "cancelled", TombstoneWriteOutcome.CancelledBeforeAdmission eventId
            "failed", TombstoneWriteOutcome.Failed CoreFault.StoreUnavailable
            "unconfirmed", TombstoneWriteOutcome.Unconfirmed eventId
        ]
        |> List.map (fun (suffix, outcome) ->
            sample
                (endpoint + "-" + suffix)
                endpoint
                (WebWireCodec.tombstoneWrite endpoint outcome))

    let all =
        reviewSamples
        @ ([
            "tombstone.approvePrune"
            "tombstone.approveTerminal"
            "tombstone.changeHold"
           ]
           |> List.collect writeSamples)
