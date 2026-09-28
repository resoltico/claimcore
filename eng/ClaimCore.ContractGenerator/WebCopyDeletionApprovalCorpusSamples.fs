namespace ClaimCore.ContractGeneration

open System
open ClaimCore.Application
open ClaimCore.Contracts

module internal WebCopyDeletionApprovalCorpusSamples =
    let private approvalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")

    let private sample id outcome =
        WebCorpusSamples.sample
            id
            "authority.approveCopyDeletion"
            (WebWireCodec.copyDeletionApproval outcome)

    let all =
        [
            sample "copy-deletion-approved" (CopyDeletionApprovalOutcome.Approved(approvalId, 3L))
            sample "copy-deletion-unavailable" CopyDeletionApprovalOutcome.ResourceUnavailable
            sample
                "copy-deletion-unconfirmed"
                (CopyDeletionApprovalOutcome.StartedUnconfirmed approvalId)
        ]
