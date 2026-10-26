namespace ClaimCore.Domain

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
        Vocabulary.define
            "token"
            [
                Role.Owner
                Role.CaseReader
                Role.CaseRegistrar
                Role.CaseHandler
                Role.PaymentRecorder
                Role.DataSteward
                Role.Auditor
            ]
            (function
             | Role.Owner -> "OWNER"
             | Role.CaseReader -> "CASE_READER"
             | Role.CaseRegistrar -> "CASE_REGISTRAR"
             | Role.CaseHandler -> "CASE_HANDLER"
             | Role.PaymentRecorder -> "PAYMENT_RECORDER"
             | Role.DataSteward -> "DATA_STEWARD"
             | Role.Auditor -> "AUDITOR")

    let definitions = Vocabulary.entries vocabulary
    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
