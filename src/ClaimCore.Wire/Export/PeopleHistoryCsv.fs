namespace ClaimCore.Wire.Export

open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

module PeopleHistoryCsv =
    let private principal (value: PrincipalKey) =
        let kind, issuer, subject = PrincipalKey.parts value
        [ Vocabulary.token PrincipalKind.vocabulary kind; issuer; subject; ""; ""; "" ]

    let private grant (value: ScopedGrant) =
        let scope, reference =
            match value.Scope with
            | GrantScope.Installation -> ScopeKind.token ScopeKind.Installation, ""
            | GrantScope.Case(_, reference) ->
                ScopeKind.token ScopeKind.Case, CaseReference.format reference

        [ ""; ""; ""; Vocabulary.token Role.vocabulary value.Role; scope; reference ]

    let private facts =
        function
        | PeopleHistoryFacts.OwnerProvisioned value
        | PeopleHistoryFacts.Registered value -> principal value
        | PeopleHistoryFacts.Granted value
        | PeopleHistoryFacts.Revoked value -> grant value
        | PeopleHistoryFacts.Enabled
        | PeopleHistoryFacts.ProfileSet _
        | PeopleHistoryFacts.ProfileErased
        | PeopleHistoryFacts.Disabled -> List.replicate 6 ""

    let values render (entry: PeopleHistoryEntry) =
        [
            OperationId.format entry.OperationId
            Sequence.format (EffectPosition.sequence entry.Position)
            string (EffectPosition.index entry.Position)
            entry.OperationHash |> EntryHash.toBytes |> System.Convert.ToHexStringLower
            Csv.instant entry.RecordedAt
            ActorId.format entry.RecordedBy.ActorId
            Csv.actorName render entry.RecordedBy
            ActorId.format entry.Target.ActorId
            Csv.actorName render entry.Target
            PeopleActKind.token (PeopleHistoryFacts.kind entry.Facts)
        ]
        @ facts entry.Facts

    let private csv = Csv.rows

    let csvHeader =
        csv
            [
                [
                    "operationId"
                    "sequence"
                    "effectIndex"
                    "operationHash"
                    "recordedAt"
                    "recordedBy.actorId"
                    "recordedBy.displayName"
                    "target.actorId"
                    "target.displayName"
                    "act"
                    "principal.kind"
                    "principal.issuer"
                    "principal.subject"
                    "role"
                    "scope.kind"
                    "caseReference"
                ]
            ]

    let csvRows render entries =
        if List.isEmpty entries then
            Array.empty<byte>
        else
            entries |> List.map (values render) |> csv
