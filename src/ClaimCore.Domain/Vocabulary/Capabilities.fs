namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type Capability =
    | ReadCase
    | ReadHistory
    | ReadHistoryFull
    | Register
    | Decide
    | RecordPayment
    | Close
    | Steward
    | ManageAuthority
    | ReadAuthority

module Capabilities =
    let vocabulary =
        Vocabulary.define
            "capability"
            [
                Capability.ReadCase
                Capability.ReadHistory
                Capability.ReadHistoryFull
                Capability.Register
                Capability.Decide
                Capability.RecordPayment
                Capability.Close
                Capability.Steward
                Capability.ManageAuthority
                Capability.ReadAuthority
            ]
            (function
             | Capability.ReadCase -> "READ_CASE"
             | Capability.ReadHistory -> "READ_HISTORY"
             | Capability.ReadHistoryFull -> "READ_HISTORY_FULL"
             | Capability.Register -> "REGISTER"
             | Capability.Decide -> "DECIDE"
             | Capability.RecordPayment -> "RECORD_PAYMENT"
             | Capability.Close -> "CLOSE"
             | Capability.Steward -> "STEWARD"
             | Capability.ManageAuthority -> "MANAGE_AUTHORITY"
             | Capability.ReadAuthority -> "READ_AUTHORITY")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
