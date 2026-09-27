namespace ClaimCore.ContractGeneration

open System
open ClaimCore.Application
open ClaimCore.Contracts

module internal WebSignerApprovalCorpusSamples =
    let private approvalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")

    let private sample id outcome =
        WebCorpusSamples.sample
            id
            "authority.approveCopySigner"
            (WebWireCodec.signerApproval outcome)

    let all =
        [
            sample "copy-signer-approved" (CopySignerApprovalOutcome.Approved(approvalId, 3L))
            sample "copy-signer-unavailable" CopySignerApprovalOutcome.ResourceUnavailable
            sample
                "copy-signer-unconfirmed"
                (CopySignerApprovalOutcome.StartedUnconfirmed approvalId)
        ]
