namespace ClaimCore.ContractGeneration

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation

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
                |> List.map (fun (_, token) ->
                    token, PresentationKeys.code (Vocabulary.name vocabulary) token)
        }

    let private fieldTerms =
        let fields = rendered BusinessField.vocabulary

        let technical =
            TaskInputId.technical
            |> List.choose (function
                | TaskInputId.Target target ->
                    Some(
                        InputTarget.token target,
                        TaskInputPresentation.messageKey (TaskInputId.Target target) "label"
                    )
                | _ -> None)

        { fields with
            Labels = fields.Labels @ technical
        }

    let private interactionTerms =
        {
            Name = TaskInputId.vocabularyName
            Labels =
                TaskInputId.extras
                |> List.map (fun input ->
                    TaskInputId.key input, TaskInputPresentation.messageKey input "label")
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
                |> List.map (fun token -> token, WorkViewsScreen.labelId token)
        }

    let private reminderStateSelection =
        {
            Name = Vocabulary.name ReminderStateSelection.vocabulary
            Labels =
                Vocabulary.entries ReminderStateSelection.vocabulary
                |> List.map (fun (selection, token) ->
                    token,
                    match ReminderStateSelection.state selection with
                    | Some state ->
                        PresentationKeys.code
                            (Vocabulary.name ReminderState.vocabulary)
                            (ReminderState.token state)
                    | None -> "reminderStateSelection.ANY.label")
        }

    let all =
        [
            rendered PresentedBoolean.vocabulary
            rendered View.vocabulary
            rendered CaseStatus.vocabulary
            rendered CaseGrouping.vocabulary
            rendered DaysOpenBand.vocabulary
            rendered MissingCaseGroup.vocabulary
            rendered ClosureOutcome.vocabulary
            rendered Lifecycle.vocabulary
            rendered Capability.vocabulary
            rendered DisclosureKind.vocabulary
            rendered DisclosureOutcome.vocabulary
            rendered Role.vocabulary
            rendered PartyRole.vocabulary
            rendered PrincipalKind.vocabulary
            rendered ActorAvailability.vocabulary
            rendered AccountState.vocabulary
            rendered RequesterKind.vocabulary
            rendered CorrectionGroupAction.vocabulary
            rendered ScopeKind.vocabulary
            rendered TaskVocabulary.vocabulary
            rendered ActFamily.vocabulary
            rendered HistoryAccess.vocabulary
            sectionTerms
            views CaseView.vocabulary
            views ReminderView.vocabulary
            rendered CaseDue.vocabulary
            rendered WorkChoice.vocabulary
            rendered ReminderState.vocabulary
            rendered DigestCountKind.vocabulary
            rendered DigestOutcome.vocabulary
            reminderStateSelection
            rendered ReminderDue.vocabulary
            rendered ReminderKind.vocabulary
            rendered DeadlineOutcome.vocabulary
            {
                Name = Vocabulary.name ReminderKindSelection.vocabulary
                Labels =
                    Vocabulary.entries ReminderKindSelection.vocabulary
                    |> List.map (fun (selection, token) ->
                        token,
                        match ReminderKindSelection.kind selection with
                        | Some kind ->
                            PresentationKeys.code
                                (Vocabulary.name ReminderKind.vocabulary)
                                (ReminderKind.token kind)
                        | None -> "reminderKindSelection.ANY.label")
            }
            rendered CaseListOrder.vocabulary
            rendered CaseAssignment.vocabulary
            {
                Name = Vocabulary.name CaseDateFilter.vocabulary
                Labels =
                    CaseDateFilter.all
                    |> List.map (fun field ->
                        CaseDateFilter.token field,
                        TaskInputPresentation.messageKey (TaskInputId.CaseDate field) "label")
            }
            rendered CaseProgress.vocabulary
            rendered PaymentDirection.vocabulary
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
                        "NOTE", "timelineKind.NOTE.label"
                        "PARTY", "timelineKind.PARTY.label"
                    ]
            }
            rendered ActSubject.vocabulary
            fieldTerms
            rendered RecordGroup.vocabulary
            interactionTerms
        ]
        |> validateRegistry
