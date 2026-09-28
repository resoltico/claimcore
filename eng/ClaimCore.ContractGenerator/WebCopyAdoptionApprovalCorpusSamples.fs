namespace ClaimCore.ContractGeneration

open System
open ClaimCore.Application
open ClaimCore.Contracts

module internal WebCopyAdoptionApprovalCorpusSamples =
    let private approvalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")

    let private sample id outcome =
        WebCorpusSamples.sample
            id
            "authority.approveCopyAdoption"
            (WebWireCodec.copyAdoptionApproval outcome)

    let all =
        [
            sample "copy-adoption-approved" (CopyAdoptionApprovalOutcome.Approved(approvalId, 3L))
            sample "copy-adoption-unavailable" CopyAdoptionApprovalOutcome.ResourceUnavailable
            sample
                "copy-adoption-unconfirmed"
                (CopyAdoptionApprovalOutcome.StartedUnconfirmed approvalId)
        ]
