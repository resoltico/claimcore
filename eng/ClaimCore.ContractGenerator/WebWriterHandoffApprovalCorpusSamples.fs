namespace ClaimCore.ContractGeneration

open System
open ClaimCore.Application
open ClaimCore.Contracts

module internal WebWriterHandoffApprovalCorpusSamples =
    let private approvalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")

    let private sample id outcome =
        WebCorpusSamples.sample
            id
            "authority.approveWriterHandoff"
            (WebWireCodec.writerHandoffApproval outcome)

    let all =
        [
            sample "writer-handoff-approved" (WriterHandoffApprovalOutcome.Approved(approvalId, 3L))
            sample "writer-handoff-unavailable" WriterHandoffApprovalOutcome.ResourceUnavailable
            sample
                "writer-handoff-unconfirmed"
                (WriterHandoffApprovalOutcome.StartedUnconfirmed approvalId)
        ]
