namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type RecordGroup =
    | Registration
    | Decision
    | Payment

module RecordGroup =
    let vocabulary =
        Vocabulary.define
            "group"
            [ RecordGroup.Registration; RecordGroup.Decision; RecordGroup.Payment ]
            (function
             | RecordGroup.Registration -> "registration"
             | RecordGroup.Decision -> "decision"
             | RecordGroup.Payment -> "payment")

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
                CorrectionGroupAction.Keep
                CorrectionGroupAction.Replace
                CorrectionGroupAction.Clear
            ]
            (function
             | CorrectionGroupAction.Keep -> "KEEP"
             | CorrectionGroupAction.Replace -> "REPLACE"
             | CorrectionGroupAction.Clear -> "CLEAR")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let forGroup =
        function
        | RecordGroup.Registration -> [ CorrectionGroupAction.Keep; CorrectionGroupAction.Replace ]
        | RecordGroup.Decision
        | RecordGroup.Payment -> all
