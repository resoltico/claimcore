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

[<RequireQualifiedAccess>]
type InputField =
    | Business of BusinessField
    | Reason

module InputField =
    let token =
        function
        | InputField.Business field -> BusinessField.token field
        | InputField.Reason -> "reason"

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
