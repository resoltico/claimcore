namespace ClaimCore.ProtocolGeneration

open ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts

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

    let private sectionTerms =
        {
            Name = Vocabulary.name CaseSection.vocabulary
            Labels =
                [
                    "registration",
                    Vocabulary.messageKey RecordGroup.vocabulary RecordGroup.Registration "label"
                    "decision",
                    Vocabulary.messageKey RecordGroup.vocabulary RecordGroup.Decision "label"
                    "payment",
                    Vocabulary.messageKey RecordGroup.vocabulary RecordGroup.Payment "label"
                    "work", "section.work.label"
                ]
        }

    let private views vocabulary =
        {
            Name = Vocabulary.name vocabulary
            Labels =
                Vocabulary.tokens vocabulary
                |> List.map (fun token -> token, "view." + token + ".label")
        }

    let all =
        [
            rendered CaseStatus.vocabulary
            rendered Lifecycle.vocabulary
            rendered Capability.vocabulary
            rendered Role.vocabulary
            rendered PrincipalKind.vocabulary
            rendered ActorAvailability.vocabulary
            rendered AccountState.vocabulary
            rendered RequesterKind.vocabulary
            rendered CorrectionGroupAction.vocabulary
            rendered ScopeKind.vocabulary
            rendered ActKind.vocabulary
            rendered ActFamily.vocabulary
            rendered InteractionEffect.vocabulary
            rendered HistoryDetail.vocabulary
            sectionTerms
            views CaseView.vocabulary
            views ReminderView.vocabulary
            rendered CaseDue.vocabulary
            rendered WorkChoice.vocabulary
            rendered WorkActKind.vocabulary
            rendered ReminderActKind.vocabulary
            rendered ReminderState.vocabulary
            rendered ReminderDue.vocabulary
            rendered CaseListOrder.vocabulary
            rendered CaseAssignment.vocabulary
            rendered CaseProgress.vocabulary
            rendered CaseStageKind.vocabulary
            rendered CaseHeadline.vocabulary
            rendered CaseAttention.vocabulary
            rendered TimelineFilter.vocabulary
            {
                Name = "timelineKind"
                Labels =
                    [
                        "CASE_FACT", "timelineKind.CASE_FACT.label"
                        "WORK", "timelineKind.WORK.label"
                        "REMINDER", "timelineKind.REMINDER.label"
                    ]
            }
            rendered ActSubject.vocabulary
            fieldTerms
            rendered RecordGroup.vocabulary
            interactionTerms
        ]
        |> validateRegistry
