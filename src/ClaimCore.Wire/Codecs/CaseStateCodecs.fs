namespace ClaimCore.Wire

open ClaimCore.Presentation

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

    let disposition =
        let reason =
            text
            |> interpret
                (fun path raw ->
                    Reason.parse raw
                    |> Result.mapError (fun _ ->
                        {
                            Path = path
                            Reason = "Invalid disposition reason."
                        }))
                Reason.format

        tagged
            "CaseDisposition"
            [
                variant
                    "case.void"
                    CaseDisposition.Voided
                    (function
                    | CaseDisposition.Voided reason -> Some reason
                    | _ -> None)
                    (record id |> field "reason" id reason)
                variant
                    "case.reinstate"
                    CaseDisposition.Reinstated
                    (function
                    | CaseDisposition.Reinstated reason -> Some reason
                    | _ -> None)
                    (record id |> field "reason" id reason)
            ]
        |> Codec.vocabulary "task"
