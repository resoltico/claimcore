namespace ClaimCore.Protocol

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

    let all =
        [
            rendered CaseStatus.vocabulary
            rendered Lifecycle.vocabulary
            rendered Role.vocabulary
            rendered PrincipalKind.vocabulary
            rendered CorrectionGroupAction.vocabulary
            rendered ScopeKind.vocabulary
            rendered ActKind.vocabulary
            rendered HistoryDetail.vocabulary
            rendered ActSubject.vocabulary
            rendered BusinessField.vocabulary
            rendered RecordGroup.vocabulary
            rendered TechnicalInput.vocabulary
        ]
        |> validateRegistry
