namespace ClaimCore.Protocol

open ClaimCore.Application.Contracts
open ClaimCore.Protocol.Presentation

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PresentedReview =
    /// The server computes presentation once from authoritative typed review facts.
    let ofCore review =
        let described = Descriptor.operationReview None review

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

        {
            Subject = subject
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
    let ofCore (reply: PreviewReply) =
        {
            PresentedPreviewReply.OperationId = reply.OperationId
            Outcome =
                Outcome.fold
                    (function
                    | Previewed.Reviewed review ->
                        Outcome.Succeeded(
                            PresentedPreviewed.Reviewed(PresentedReview.ofCore review)
                        )
                    | Previewed.AlreadyAccepted receipt ->
                        Outcome.Succeeded(PresentedPreviewed.AlreadyAccepted receipt))
                    Outcome.Rejected
                    Outcome.Failed
                    reply.Outcome
        }
