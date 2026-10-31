namespace ClaimCore.ProtocolGeneration

open ClaimCore.Protocol

open ClaimCore.Domain

type TermVocabulary =
    {
        Name: string
        Labels: (string * string) list
    }

/// Each rendered vocabulary owns its namespace; equal tokens in different vocabularies are valid.
module Terms =
    let validateRegistry entries =
        let names = entries |> List.map _.Name

        if List.distinct names <> names then
            invalidOp "Rendered vocabulary names must be distinct."

        for entry in entries do
            let tokens = entry.Labels |> List.map fst

            if List.isEmpty tokens || List.distinct tokens <> tokens then
                invalidOp "A rendered vocabulary requires unique nonempty token entries."

        entries

    let private rendered vocabulary =
        {
            Name = Vocabulary.name vocabulary
            Labels =
                Vocabulary.entries vocabulary
                |> List.map (fun (value, token) ->
                    token, Vocabulary.messageKey vocabulary value "label")
        }

    let private fieldTerms =
        let fields = rendered BusinessField.vocabulary

        let technical =
            InteractionInput.technical
            |> List.choose (function
                | InteractionInput.Target target ->
                    Some(
                        InputTarget.token target,
                        InteractionInput.messageKey (InteractionInput.Target target) "label"
                    )
                | _ -> None)

        { fields with
            Labels = fields.Labels @ technical
        }

    let private interactionTerms =
        {
            Name = InteractionInput.vocabularyName
            Labels =
                InteractionInput.extras
                |> List.map (fun input ->
                    InteractionInput.key input, InteractionInput.messageKey input "label")
        }

    let all =
        [
            rendered CaseStatus.vocabulary
            rendered Lifecycle.vocabulary
            rendered Capability.vocabulary
            rendered Role.vocabulary
            rendered PrincipalKind.vocabulary
            rendered CorrectionGroupAction.vocabulary
            rendered ScopeKind.vocabulary
            rendered ActKind.vocabulary
            rendered HistoryDetail.vocabulary
            rendered CaseListOrder.vocabulary
            rendered CaseAssignment.vocabulary
            rendered CaseProgress.vocabulary
            rendered ActSubject.vocabulary
            fieldTerms
            rendered RecordGroup.vocabulary
            interactionTerms
        ]
        |> validateRegistry
