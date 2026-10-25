namespace ClaimCore.Domain

open ClaimCore.Contract

/// Allowed correction actions are a domain rule; labels, ordering and prefill belong to the generator.
module ActDefinitions =
    let correctionActions =
        function
        | CorrectionGroup.Registration ->
            [ CorrectionGroupAction.Keep; CorrectionGroupAction.Replace ]
        | CorrectionGroup.Decision
        | CorrectionGroup.Payment ->
            [
                CorrectionGroupAction.Keep
                CorrectionGroupAction.Replace
                CorrectionGroupAction.Clear
            ]
