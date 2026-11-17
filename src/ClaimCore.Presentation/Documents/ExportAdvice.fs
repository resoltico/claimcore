namespace ClaimCore.Presentation

/// Resource failures of the bounded download retain shared meaning in every client.
module ExportAdvice =
    let private advice key action =
        {
            HumanAdvice.Message =
                {
                    MessageId = key
                    Arguments = Map.empty
                }
            Action = action
        }

    let limitExceeded =
        advice "label.exportLimitExceeded" RecommendedAction.CorrectInput

    let processingTimeout =
        advice "label.exportProcessingTimeout" RecommendedAction.RetrySafe

    let invalidContinuation =
        advice "label.exportInvalidContinuation" RecommendedAction.StopAndInvestigate
