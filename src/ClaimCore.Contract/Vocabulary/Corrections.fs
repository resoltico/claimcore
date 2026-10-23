namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type CorrectionGroup =
    | Registration
    | Decision
    | Payment

module CorrectionGroup =
    let token =
        function
        | CorrectionGroup.Registration -> "registration"
        | CorrectionGroup.Decision -> "decision"
        | CorrectionGroup.Payment -> "payment"

module TextFormat =
    [<Literal>]
    let MaximumFreeTextBytes = 1024

[<RequireQualifiedAccess>]
type CorrectionGroupAction =
    | Keep
    | Replace
    | Clear

module CorrectionActions =
    let token =
        function
        | CorrectionGroupAction.Keep -> "KEEP"
        | CorrectionGroupAction.Replace -> "REPLACE"
        | CorrectionGroupAction.Clear -> "CLEAR"
