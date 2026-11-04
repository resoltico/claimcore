namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol.Presentation

/// Render only typed core differences; history and disclosure are never reconstructed here.
module internal TextTimelineReply =
    let page language (page: CaseTimelinePage) =
        [
            TextReplyFormat.displayed language (Some(CaseReference.format page.Reference))
        ]
        @ (page.Items
           |> List.collect (Descriptor.timelineEntry >> TextDescriptorValue.sections language))
        @ TextReplyFormat.nextLines language page.NextCursor
        @ [ TextObservation.line language page.Basis ]
