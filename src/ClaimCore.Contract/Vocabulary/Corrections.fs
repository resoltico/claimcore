namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type CorrectionGroup =
    | Registration
    | Decision
    | Payment

module CorrectionGroup =
    let vocabulary =
        Vocabulary.define
            "group"
            [
                CorrectionGroup.Registration, "registration"
                CorrectionGroup.Decision, "decision"
                CorrectionGroup.Payment, "payment"
            ]

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type CorrectionGroupAction =
    | Keep
    | Replace
    | Clear

module CorrectionActions =
    let vocabulary =
        Vocabulary.define
            "token"
            [
                CorrectionGroupAction.Keep, "KEEP"
                CorrectionGroupAction.Replace, "REPLACE"
                CorrectionGroupAction.Clear, "CLEAR"
            ]

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
