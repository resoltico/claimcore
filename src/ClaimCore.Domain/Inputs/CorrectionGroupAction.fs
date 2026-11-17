namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type CorrectionGroupAction =
    | Keep
    | Update
    | Clear

/// Closed operation vocabulary; names and payload kinds belong to the core, not a renderer.
module CorrectionGroupAction =
    let vocabulary =
        Vocabulary.ofUnion<CorrectionGroupAction> "correction" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let actions =
        function
        | RecordGroup.Registration -> [ CorrectionGroupAction.Keep; CorrectionGroupAction.Update ]
        | RecordGroup.Decision ->
            [
                CorrectionGroupAction.Keep
                CorrectionGroupAction.Update
                CorrectionGroupAction.Clear
            ]
        | RecordGroup.Payment -> []
