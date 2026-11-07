namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation

/// Render only typed core differences; history and disclosure are never reconstructed here.
module internal TextTimelineReply =
    let page language (page: CaseTimelinePage) =
        let header, rows =
            TextTable.page language (TextTable.environment ()) Descriptor.timelineColumns page.Items

        [
            TextReplyFormat.displayed language (Some(CaseReference.format page.Reference))
        ]
        @ (if header = "" then [] else [ header ])
        @ (page.Items
           |> List.collect (fun item ->
               rows [ item ]
               @ (Descriptor.timelineEntry item
                  |> List.tail
                  |> TextDescriptorValue.sections language)))
        @ TextObservation.footer language page.Items.Length page.NextCursor page.Basis true
