namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type CaseReportDate =
    | Registered
    | Received
    | Closed
    | LastActivity

[<RequireQualifiedAccess>]
type CaseDateBound =
    | From
    | Through

[<RequireQualifiedAccess>]
type CaseDateFilter =
    | RegisteredFrom
    | RegisteredThrough
    | ReceivedFrom
    | ReceivedThrough
    | ClosedFrom
    | ClosedThrough
    | InactiveSince

module CaseDateFilter =
    let vocabulary =
        Vocabulary.ofUnion<CaseDateFilter> "caseDateFilter" TokenCasing.CamelCase None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let bound =
        function
        | CaseDateFilter.RegisteredFrom -> CaseReportDate.Registered, CaseDateBound.From
        | CaseDateFilter.RegisteredThrough -> CaseReportDate.Registered, CaseDateBound.Through
        | CaseDateFilter.ReceivedFrom -> CaseReportDate.Received, CaseDateBound.From
        | CaseDateFilter.ReceivedThrough -> CaseReportDate.Received, CaseDateBound.Through
        | CaseDateFilter.ClosedFrom -> CaseReportDate.Closed, CaseDateBound.From
        | CaseDateFilter.ClosedThrough -> CaseReportDate.Closed, CaseDateBound.Through
        | CaseDateFilter.InactiveSince -> CaseReportDate.LastActivity, CaseDateBound.Through

    let matches filter limit actual =
        limit
        |> Option.forall (fun limit ->
            actual
            |> Option.exists (fun actual ->
                match snd (bound filter) with
                | CaseDateBound.From -> actual >= limit
                | CaseDateBound.Through -> actual <= limit))

type CaseReportDates =
    {
        Registered: CalendarDate
        Closed: CalendarDate option
        LastActivity: CalendarDate
    }

module CaseReportDates =
    let record date (status: CaseStatus option) previous =
        {
            Registered = previous |> Option.map _.Registered |> Option.defaultValue date
            Closed =
                if status = Some CaseStatus.Closed then
                    previous |> Option.bind _.Closed |> Option.orElse (Some date)
                else
                    None
            LastActivity = date
        }

    let value (dates: CaseReportDates) received =
        function
        | CaseReportDate.Registered -> Some dates.Registered
        | CaseReportDate.Received -> received
        | CaseReportDate.Closed -> dates.Closed
        | CaseReportDate.LastActivity -> Some dates.LastActivity

[<RequireQualifiedAccess>]
type CaseGrouping =
    | RegisteredMonth
    | ReceivedMonth
    | ClosedMonth
    | Outcome
    | Responsible
    | DaysOpen

module CaseGrouping =
    let vocabulary =
        Vocabulary.ofUnion<CaseGrouping> "caseGrouping" TokenCasing.ScreamingSnake None

[<RequireQualifiedAccess>]
type DaysOpenBand =
    | Through30
    | Through90
    | Through180
    | Through365
    | Over365

module DaysOpenBand =
    let bounds =
        [
            DaysOpenBand.Through30, 30
            DaysOpenBand.Through90, 90
            DaysOpenBand.Through180, 180
            DaysOpenBand.Through365, 365
        ]

    let vocabulary =
        Vocabulary.define
            "daysOpenBand"
            [ DaysOpenBand.Through30; DaysOpenBand.Through90; DaysOpenBand.Through180; DaysOpenBand.Through365; DaysOpenBand.Over365 ]
            (function
            | DaysOpenBand.Through30 -> "0-30"
            | DaysOpenBand.Through90 -> "31-90"
            | DaysOpenBand.Through180 -> "91-180"
            | DaysOpenBand.Through365 -> "181-365"
            | DaysOpenBand.Over365 -> "366+")

    let ofDays days =
        bounds
        |> List.tryPick (fun (band, upper) -> if days <= upper then Some band else None)
        |> Option.defaultValue DaysOpenBand.Over365

    let daysOpen today (dates: CaseReportDates) =
        let finalDate = dates.Closed |> Option.defaultValue today
        (CalendarDate.value finalDate).DayNumber - (CalendarDate.value dates.Registered).DayNumber

[<RequireQualifiedAccess>]
type MissingCaseGroup =
    | NotRecorded
    | NotClosed
    | Unassigned

module MissingCaseGroup =
    let vocabulary =
        Vocabulary.ofUnion<MissingCaseGroup> "missingCaseGroup" TokenCasing.ScreamingSnake None

    let forGrouping =
        function
        | CaseGrouping.ReceivedMonth -> Some MissingCaseGroup.NotRecorded
        | CaseGrouping.ClosedMonth
        | CaseGrouping.Outcome -> Some MissingCaseGroup.NotClosed
        | CaseGrouping.Responsible -> Some MissingCaseGroup.Unassigned
        | CaseGrouping.RegisteredMonth
        | CaseGrouping.DaysOpen -> None

[<Struct>]
type CaseMonth = private CaseMonth of CalendarDate

module CaseMonth =
    let parse (raw: string) =
        CalendarDate.parse (raw + "-01") |> Result.map CaseMonth

    let format (CaseMonth date) = (CalendarDate.format date).Substring(0, 7)
