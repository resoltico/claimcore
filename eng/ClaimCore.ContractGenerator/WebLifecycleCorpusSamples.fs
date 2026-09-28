namespace ClaimCore.ContractGeneration

open System
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain

module internal WebLifecycleCorpusSamples =
    let private eventId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")
    let private holdId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")

    let private review =
        {
            BusinessRevision = 1L
            LifecycleSequence = 0L
            LifecycleHash = String.replicate 64 "0"
            Disposition = CaseDisposition.Active
            PrivacyPhase = PrivacyPhase.Active
            ActiveHolds =
                [
                    {
                        HoldId = holdId
                        ReviewOn = DateOnly(2026, 10, 1)
                    }
                ]
            VoidRequiresTwoApprovals = false
        }

    let private sample id endpoint bytes =
        WebCorpusSamples.sample id endpoint bytes

    let private reviewSamples =
        [
            sample
                "lifecycle-review-available"
                "lifecycle.review"
                (WebWireCodec.lifecycleReview (LifecycleReviewOutcome.Available review))
            sample
                "lifecycle-review-unavailable"
                "lifecycle.review"
                (WebWireCodec.lifecycleReview LifecycleReviewOutcome.ResourceUnavailable)
            sample
                "lifecycle-review-cancelled"
                "lifecycle.review"
                (WebWireCodec.lifecycleReview LifecycleReviewOutcome.Cancelled)
            sample
                "lifecycle-review-failed"
                "lifecycle.review"
                (WebWireCodec.lifecycleReview (
                    LifecycleReviewOutcome.Failed CoreFault.StoreUnavailable
                ))
        ]

    let private writeSamples endpoint =
        [
            "applied", LifecycleWriteOutcome.Applied(eventId, 2L, 1L)
            "refused", LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalRequired
            "hold-capacity", LifecycleWriteOutcome.Refused LifecycleRefusal.HoldCapacityExceeded
            "approval-capacity",
            LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalCapacityExceeded
            "unavailable", LifecycleWriteOutcome.ResourceUnavailable
            "cancelled", LifecycleWriteOutcome.CancelledBeforeAdmission eventId
            "failed", LifecycleWriteOutcome.Failed CoreFault.StoreUnavailable
            "unconfirmed", LifecycleWriteOutcome.Unconfirmed eventId
        ]
        |> List.map (fun (suffix, outcome) ->
            sample
                (endpoint + "-" + suffix)
                endpoint
                (WebWireCodec.lifecycleWrite endpoint outcome))

    let all =
        reviewSamples
        @ ([ "lifecycle.apply"; "lifecycle.approve" ] |> List.collect writeSamples)
