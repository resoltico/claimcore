namespace ClaimCore.Presentation

open ClaimCore.Application.Contracts
open ClaimCore.Presentation

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PresentedReview =
    /// The server computes presentation once from authoritative typed review facts.
    let ofCore input review =
        let described = ReviewScreen.operationReview input review

        let subject =
            match review with
            | OperationReview.Case value ->
                PresentedReviewSubject.Case
                    {
                        Before = value.Before
                        Proposed = value.Proposed
                        Context = value.Context
                    }
            | OperationReview.Work value -> PresentedReviewSubject.Work value
            | OperationReview.Reminder value -> PresentedReviewSubject.Reminder value
            | OperationReview.Authority value -> PresentedReviewSubject.Authority value

        let parentFacts =
            match review with
            | OperationReview.Work value -> CaseScreen.caseFacts value.Parent
            | OperationReview.Reminder value -> CaseScreen.caseFacts value.Parent
            | _ -> []

        let sections = described.Effects @ parentFacts

        let details =
            sections |> List.collect _.Entries |> List.filter ScreenEntries.technical

        let facts =
            sections
            |> List.map (fun section ->
                { section with
                    Entries = section.Entries |> List.filter (ScreenEntries.technical >> not)
                })

        {
            Subject = subject
            View =
                Screen.create (
                    ScreenEntries.factRegions facts
                    @ (if details.IsEmpty then
                           []
                       else
                           [
                               ScreenEntries.entryRegion
                                   RegionRole.Details
                                   (Some "label.recordDetails")
                                   details
                           ])
                )
            Presentation =
                {
                    Changes = described.Changes
                    Unchanged =
                        described.Unchanged
                        |> List.map (fun entry ->
                            {
                                PresentedField.Key = entry.Key
                                LabelId = entry.LabelId
                            })
                }
        }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PresentedPreviewReply =
    let ofCore input (reply: PreviewReply) =
        {
            PresentedPreviewReply.OperationId = reply.OperationId
            Outcome =
                Outcome.fold
                    (function
                    | Previewed.Reviewed review ->
                        Outcome.Succeeded(
                            PresentedPreviewed.Reviewed(PresentedReview.ofCore input review)
                        )
                    | Previewed.AlreadyAccepted receipt ->
                        Outcome.Succeeded(
                            PresentedPreviewed.AlreadyAccepted(
                                Presented.create ReceiptScreen.view receipt
                            )
                        ))
                    Outcome.Rejected
                    Outcome.Failed
                    reply.Outcome
        }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PresentedExecuteReply =
    let ofCore (reply: ExecuteReply) =
        let receipt = Presented.create ReceiptScreen.view

        {
            PresentedExecuteReply.OperationId = reply.OperationId
            Outcome =
                match reply.Outcome with
                | ExecuteResult.Accepted value -> PresentedExecuteResult.Accepted(receipt value)
                | ExecuteResult.AlreadyAccepted value ->
                    PresentedExecuteResult.AlreadyAccepted(receipt value)
                | ExecuteResult.Rejected value -> PresentedExecuteResult.Rejected value
                | ExecuteResult.Failed value -> PresentedExecuteResult.Failed value
                | ExecuteResult.OutcomeUnknown -> PresentedExecuteResult.OutcomeUnknown
        }

module PresentedLookupReply =
    let ofCore (reply: OperationReply<OperationLookupResult>) =
        let success =
            function
            | OperationLookupResult.Accepted(receipt, byRequester) ->
                PresentedLookupResult.Accepted(
                    Presented.create ReceiptScreen.view receipt,
                    byRequester
                )
            | OperationLookupResult.Pending finalAt ->
                PresentedLookupResult.Pending(finalAt, OperationScreen.pending finalAt)
            | OperationLookupResult.NotAccepted ->
                PresentedLookupResult.NotAccepted(OperationScreen.notAccepted ())

        {
            OperationReply.OperationId = reply.OperationId
            Outcome =
                Outcome.fold
                    (success >> Outcome.Succeeded)
                    Outcome.Rejected
                    Outcome.Failed
                    reply.Outcome
        }
