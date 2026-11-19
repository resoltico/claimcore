namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Json

/// CSV is an exchange media type, not a third semantic representation.
[<RequireQualifiedAccess>]
type ExportSelection =
    | Cases of CaseListFilter<string>
    | CaseSummary of CaseSummarySelection
    | Timeline of CaseTimelineRequest
    | Reminders of ReminderListRequest
    | PeopleHistory of PeopleHistoryRequest

[<RequireQualifiedAccess>]
type ExportFailure =
    | Rejected of HumanRejection
    | Failed of CoreFault
    | LimitExceeded of HumanAdvice
    | ProcessingTimeout of HumanAdvice
    | InvalidContinuation of HumanAdvice

/// A successful complete document is encoded directly as CSV; only failures have JSON grammar.
module ExportTransport =
    open Codec
    let token = "export"
    let path = "/api/" + token
    let mediaType = "text/csv; charset=utf-8"
    let successMediaType = "text/csv"

    let request =
        taggedOn
            "query"
            "ExportRequest"
            [
                variant
                    "case.list"
                    ExportSelection.Cases
                    (function
                    | ExportSelection.Cases value -> Some value
                    | _ -> None)
                    (record id |> field "selection" id RequestCodecs.caseFilter)
                variant
                    "case.summary"
                    ExportSelection.CaseSummary
                    (function
                    | ExportSelection.CaseSummary value -> Some value
                    | _ -> None)
                    (record id |> field "selection" id RequestCodecs.caseSummarySelection)
                variant
                    "case.timeline"
                    ExportSelection.Timeline
                    (function
                    | ExportSelection.Timeline value -> Some value
                    | _ -> None)
                    (record id |> field "selection" id RequestCodecs.timelineSelection)
                variant
                    "reminder.list"
                    ExportSelection.Reminders
                    (function
                    | ExportSelection.Reminders value -> Some value
                    | _ -> None)
                    (record id |> field "selection" id RequestCodecs.reminderSelection)
                variant
                    "people.history"
                    ExportSelection.PeopleHistory
                    (function
                    | ExportSelection.PeopleHistory value -> Some value
                    | _ -> None)
                    (record id |> field "selection" id RequestCodecs.peopleHistorySelection)
            ]

    let private outcome =
        taggedOn
            "kind"
            "ExportFailure"
            [
                variant
                    "REJECTED"
                    ExportFailure.Rejected
                    (function
                    | ExportFailure.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "rejection" id HumanDocumentCodecs.rejection)
                variant
                    "FAILED"
                    ExportFailure.Failed
                    (function
                    | ExportFailure.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "fault" id DiagnosticCodecs.fault)
                variant
                    "LIMIT_EXCEEDED"
                    ExportFailure.LimitExceeded
                    (function
                    | ExportFailure.LimitExceeded value -> Some value
                    | _ -> None)
                    (record id |> field "advice" id PresentationCodecs.advice)
                variant
                    "PROCESSING_TIMEOUT"
                    ExportFailure.ProcessingTimeout
                    (function
                    | ExportFailure.ProcessingTimeout value -> Some value
                    | _ -> None)
                    (record id |> field "advice" id PresentationCodecs.advice)
                variant
                    "INVALID_CONTINUATION"
                    ExportFailure.InvalidContinuation
                    (function
                    | ExportFailure.InvalidContinuation value -> Some value
                    | _ -> None)
                    (record id |> field "advice" id PresentationCodecs.advice)
            ]

    let failure =
        record id |> field "outcome" id outcome |> build |> named "ExportFailureReply"

    let filenamesByQuery =
        Map.ofList
            [
                QueryTaskId.key QueryTaskId.CaseList, "claimcore-cases.csv"
                QueryTaskId.key QueryTaskId.CaseSummary, "claimcore-case-summary.csv"
                QueryTaskId.key QueryTaskId.CaseTimeline, "claimcore-case-timeline.csv"
                QueryTaskId.key QueryTaskId.ReminderList, "claimcore-reminders.csv"
                QueryTaskId.key QueryTaskId.PeopleHistory, "claimcore-people-history.csv"
            ]

    let filenames = filenamesByQuery |> Map.values |> Seq.toList

    let filename selection =
        let query =
            match selection with
            | ExportSelection.Cases _ -> QueryTaskId.CaseList
            | ExportSelection.CaseSummary _ -> QueryTaskId.CaseSummary
            | ExportSelection.Timeline _ -> QueryTaskId.CaseTimeline
            | ExportSelection.Reminders _ -> QueryTaskId.ReminderList
            | ExportSelection.PeopleHistory _ -> QueryTaskId.PeopleHistory

        filenamesByQuery[QueryTaskId.key query]
