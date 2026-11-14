namespace ClaimCore.Cli

open ClaimCore.Wire.Export

open ClaimCore.CommandLine

open System.Text
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

/// Live page traversal retains the request filters and refuses repeated continuations.
module internal WorkListCommand =
    let private runPages
        (io: ReadPageIO)
        (prepared: PreparedVerb)
        (endpoint: Endpoint<'request, RepresentationReply<HumanReadReply, Outcome<'page>>>)
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

                let frame =
                    Frames.withRepresentation
                        (if prepared.Format = VerbFormat.Text then
                             Representation.Human
                         else
                             Representation.Data)
                        frame

                let! response = io.Exchange frame token

                let result =
                    match response.Frame.Result with
                    | FrameResult.Reply reply -> ServiceReply.value endpoint reply
                    | _ -> None

                let next =
                    match result with
                    | Some(RepresentationReply.Data(Outcome.Succeeded page)) ->
                        if prepared.Format = VerbFormat.Csv then
                            io.Output(csvRows page)
                        else
                            io.Deliver frame response

                        Some(getNext page)
                    | Some(RepresentationReply.Human {
                                                         Outcome = HumanReadOutcome.Succeeded document
                                                     }) ->
                        io.Deliver frame response
                        Some(document.NextCursor)
                    | _ -> None

                match next with
                | Some next ->
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
                | None ->
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

        if (Frames.endpoint initial).Id = Api.casePayments.Id then
            let request = Frames.value Api.casePayments initial |> Option.get

            runPages
                io
                prepared
                Api.casePayments
                (fun (request: CasePaymentsRequest) -> request.AfterCursor)
                (fun cursor request -> { request with AfterCursor = cursor })
                (fun (page: CasePaymentPage) -> page.NextCursor)
                (fun _ -> Array.empty<byte>)
                request
                token
        elif (Frames.endpoint initial).Id = Api.caseNotes.Id then
            let request = Frames.value Api.caseNotes initial |> Option.get

            runPages
                io
                prepared
                Api.caseNotes
                (fun (request: CaseNotesRequest) -> request.AfterCursor)
                (fun cursor request -> { request with AfterCursor = cursor })
                (fun (page: CaseNotesPage) -> page.NextCursor)
                (fun _ -> Array.empty<byte>)
                request
                token
        elif (Frames.endpoint initial).Id = Api.reminders.Id then
            let request = Frames.value Api.reminders initial |> Option.get

            if prepared.Format = VerbFormat.Csv then
                io.Output ReminderCsv.csvHeader

            runPages
                io
                prepared
                Api.reminders
                (fun (request: ReminderListRequest) -> request.AfterCursor)
                (fun cursor request -> { request with AfterCursor = cursor })
                (fun (page: ReminderPage) -> page.NextCursor)
                (fun page ->
                    ReminderCsv.csvRows
                        (MessageCatalogs.render LocalizationPolicy.defaultLanguage)
                        page.Items)
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
