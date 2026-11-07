namespace ClaimCore.Cli

open System.Text
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

/// Live page traversal retains the request filters and refuses repeated continuations.
module internal WorkListCommand =
    let private csv row =
        String.concat "," (List.map CsvCell.format row) + "\r\n"
        |> Encoding.UTF8.GetBytes

    let private reminderRows rows =
        rows
        |> List.map (fun (row: ReminderReadModel) ->
            let value = row.Reminder

            csv
                [
                    CaseId.format (Reminder.caseId value)
                    CaseReference.format row.Reference
                    ReminderId.format (Reminder.id value)
                    Revision.format (Reminder.revision value)
                    ReminderAction.format (Reminder.action value)
                    CalendarDate.format (Reminder.due value)
                    ReminderState.token (Reminder.state value)
                    ReplyTextFormat.instant row.RecordedAt
                    ActorId.format row.RecordedBy.ActorId
                    ReplyTextFormat.actorName LocalizationPolicy.defaultLanguage row.RecordedBy
                    ReplyTextFormat.boolean row.Active
                ])
        |> Array.concat

    let private runPages
        (io: ReadPageIO)
        (prepared: PreparedVerb)
        (endpoint: Endpoint<'request, Outcome<Presented<'page>>>)
        (getCursor: 'request -> string option)
        (setCursor: string option -> 'request -> 'request)
        (getNext: 'page -> string option)
        (csvRows: 'page -> byte array)
        (request: 'request)
        (token: CancellationToken)
        =
        task {
            let mutable cursor = getCursor request
            let mutable running = true
            let mutable code = ExitCode.value ExitCode.Success
            let seen = System.Collections.Generic.HashSet<string>()

            while running && not token.IsCancellationRequested do
                let frame =
                    Frames.create endpoint (setCursor cursor request)
                    |> Result.defaultWith (fun _ ->
                        invalidOp "A typed live page must remain admitted.")

                let! response = io.Exchange frame token

                let result =
                    match response.Frame.Result with
                    | FrameResult.Reply reply -> ServiceReply.value endpoint reply
                    | _ -> None

                match result with
                | Some(Outcome.Succeeded presented) ->
                    let page = presented.Data

                    if prepared.Format = VerbFormat.Csv then
                        io.Output(csvRows page)
                    else
                        io.Deliver frame response

                    let next = getNext page

                    if prepared.All && next.IsSome then
                        if next = cursor || not (seen.Add next.Value) then
                            invalidOp "A live page continuation must advance."

                        cursor <- next
                    else
                        if prepared.Format = VerbFormat.Csv then
                            let lines = ReplyTextFormat.nextLines prepared.Language next

                            if not lines.IsEmpty then
                                io.Errors(Encoding.UTF8.GetBytes(String.concat "\n" lines + "\n"))

                        running <- false
                | _ ->
                    if prepared.Format = VerbFormat.Csv then
                        io.Errors(ReplyText.bytesFor prepared.Language (Some frame) response.Frame)
                    else
                        io.Deliver frame response

                    code <- response.ExitCode
                    running <- false

            return
                if token.IsCancellationRequested then
                    ExitCode.value ExitCode.Cancelled
                else
                    code
        }

    let run (io: ReadPageIO) (prepared: PreparedVerb) (token: CancellationToken) =
        let initial = prepared.Invocation

        if (Frames.endpoint initial).Id = Api.reminders.Id then
            let request = Frames.value Api.reminders initial |> Option.get

            if prepared.Format = VerbFormat.Csv then
                io.Output(
                    csv
                        [
                            "caseId"
                            "caseReference"
                            "reminderId"
                            "revision"
                            "action"
                            "dueDate"
                            "state"
                            "recordedAt"
                            "recordedBy.actorId"
                            "recordedBy.displayName"
                            "active"
                        ]
                )

            runPages
                io
                prepared
                Api.reminders
                (fun (request: ReminderListRequest) -> request.AfterCursor)
                (fun cursor request -> { request with AfterCursor = cursor })
                (fun (page: ReminderPage) -> page.NextCursor)
                (fun page -> reminderRows page.Items)
                request
                token
        else
            let request = Frames.value Api.caseTimeline initial |> Option.get

            if prepared.Format = VerbFormat.Csv then
                io.Output TimelineCsv.csvHeader

            runPages
                io
                prepared
                Api.caseTimeline
                (fun (request: CaseTimelineRequest) -> request.AfterCursor)
                (fun cursor request -> { request with AfterCursor = cursor })
                (fun (page: CaseTimelinePage) -> page.NextCursor)
                TimelineCsv.csvRows
                request
                token
