namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module internal CaseStateCodecs =
    open Codec

    let state name fields =
        tagged
            name
            [
                variant
                    "ACTIVE"
                    CaseState.Active
                    (function
                    | CaseState.Active value -> Some value
                    | _ -> None)
                    fields
                variant
                    "VOIDED"
                    CaseState.Voided
                    (function
                    | CaseState.Voided value -> Some value
                    | _ -> None)
                    fields
                unitVariant "ERASED" CaseState.Erased
            ]
