namespace ClaimCore.Cli

open ClaimCore.Application.Contracts
open ClaimCore.Protocol.Presentation

module internal TextContextReply =
    let context language (reply: ContextReply) =
        TextDescriptorValue.sections language (Descriptor.context reply)
        @ (match reply.Requester with
           | RequesterState.Active item -> [ TextObservation.line language item.Runtime.Basis ]
           | RequesterState.NoRoles _
           | RequesterState.Disabled _
           | RequesterState.NotRegistered _ -> [])

    let summary language (reply: CaseWorklistSummary) =
        TextDescriptorValue.sections language (Descriptor.caseTotals reply)
        @ [ TextObservation.line language reply.Basis ]
