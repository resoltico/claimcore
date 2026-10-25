namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type Role =
    | Owner
    | CaseReader
    | CaseRegistrar
    | CaseHandler
    | PaymentRecorder
    | DataSteward
    | Auditor

module RoleTokens =
    let vocabulary =
        [
            Role.Owner, "OWNER"
            Role.CaseReader, "CASE_READER"
            Role.CaseRegistrar, "CASE_REGISTRAR"
            Role.CaseHandler, "CASE_HANDLER"
            Role.PaymentRecorder, "PAYMENT_RECORDER"
            Role.DataSteward, "DATA_STEWARD"
            Role.Auditor, "AUDITOR"
        ]
        |> Vocabulary.define "token"

    let definitions = Vocabulary.entries vocabulary
    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
