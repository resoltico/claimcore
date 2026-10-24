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
    let definitions =
        [
            Role.Owner, "OWNER"
            Role.CaseReader, "CASE_READER"
            Role.CaseRegistrar, "CASE_REGISTRAR"
            Role.CaseHandler, "CASE_HANDLER"
            Role.PaymentRecorder, "PAYMENT_RECORDER"
            Role.DataSteward, "DATA_STEWARD"
            Role.Auditor, "AUDITOR"
        ]

    let all = definitions |> List.map fst

    let token role =
        definitions |> List.find (fst >> (=) role) |> snd

    let tryParse value =
        definitions |> List.tryFind (snd >> (=) value) |> Option.map fst
