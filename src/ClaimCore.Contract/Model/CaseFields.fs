namespace ClaimCore.Contract

/// Accepted facts share the authored group types; a decision is complete or absent.
type CaseFields =
    {
        CaseReference: string
        Status: CaseStatus
        Registration: Registration
        Decision: Decision option
        Payment: Payment option
    }

/// Lifecycle is independent of the payload carried by a full view or summary.
[<RequireQualifiedAccess>]
type CaseState<'fields> =
    | Active of 'fields
    | Voided of 'fields
    | Erased

module CaseState =
    let map mapping =
        function
        | CaseState.Active fields -> CaseState.Active(mapping fields)
        | CaseState.Voided fields -> CaseState.Voided(mapping fields)
        | CaseState.Erased -> CaseState.Erased

    let fields =
        function
        | CaseState.Active fields
        | CaseState.Voided fields -> Some fields
        | CaseState.Erased -> None

    let vocabulary =
        Vocabulary.define
            "token"
            [
                CaseState.Active(), "ACTIVE"
                CaseState.Voided(), "VOIDED"
                CaseState.Erased, "ERASED"
            ]

    let all = Vocabulary.all vocabulary

    let token state =
        Vocabulary.token vocabulary (map ignore state)

/// A disclosure cannot carry an erased state.
[<RequireQualifiedAccess>]
type LiveCase =
    | Active of CaseFields
    | Voided of CaseFields

module LiveCase =
    let toState =
        function
        | LiveCase.Active fields -> CaseState.Active fields
        | LiveCase.Voided fields -> CaseState.Voided fields

    let ofState =
        function
        | CaseState.Active fields -> Some(LiveCase.Active fields)
        | CaseState.Voided fields -> Some(LiveCase.Voided fields)
        | CaseState.Erased -> None

type CaseSnapshot =
    {
        Reference: string
        Revision: int64
        State: CaseState<CaseFields>
    }
