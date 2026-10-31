namespace ClaimCore.Cli

open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json

module internal CaseListCommand =
    let private text =
        function
        | [] -> Array.empty<byte>
        | lines -> String.concat "\n" lines + "\n" |> Encoding.UTF8.GetBytes

    let private invocation request =
        Frames.create Api.caseList request
        |> Result.defaultWith (fun _ ->
            invalidOp "A typed case page must produce an admitted invocation.")

    let private page (reply: CliReply) =
        match reply.Frame.Result with
        | FrameResult.Reply value when value.Route.Id = Api.caseList.Id ->
            ServiceReply.value Api.caseList value
        | FrameResult.Reply _ -> invalidOp "A case page must retain its requested endpoint."
        | _ -> None

    let private header (io: ReadPageIO) (prepared: PreparedVerb) =
        match prepared.Format with
        | VerbFormat.Csv -> io.Output CaseListRows.csvHeader
        | VerbFormat.Text -> io.Output(text [ CaseListRows.textHeader prepared.Language ])
        | VerbFormat.Json -> ()

    let private rows
        (io: ReadPageIO)
        (prepared: PreparedVerb)
        frame
        response
        (page: CaseSummaryPage)
        =
        match prepared.Format with
        | VerbFormat.Csv -> io.Output(CaseListRows.csvRows page.Items)
        | VerbFormat.Text -> io.Output(text (CaseListRows.textRows prepared.Language page.Items))
        | VerbFormat.Json -> io.Deliver frame response

    let private failure (io: ReadPageIO) (prepared: PreparedVerb) cursor frame response =
        if prepared.Format = VerbFormat.Csv then
            io.Errors(TextReply.bytesFor prepared.Language (Some frame) response.Frame)
        else
            io.Deliver frame response

        let reached = defaultArg cursor "—"

        MessageCatalogs.renderCli prepared.Language (CliMessage.CursorReached reached)
        |> fun value -> io.Errors(text [ value ])

    let run (io: ReadPageIO) (prepared: PreparedVerb) (token: CancellationToken) =
        task {
            let initial = prepared.Invocation

            let request =
                Frames.value Api.caseList initial
                |> Option.defaultWith (fun () ->
                    invalidOp "A listing must carry a case-list request.")

            let mutable cursor = request.Page.AfterCursor
            let mutable running = true
            let mutable code = ExitCode.value ExitCode.Success
            header io prepared

            while running && not token.IsCancellationRequested do
                let current =
                    { request with
                        Page =
                            { request.Page with
                                AfterCursor = cursor
                            }
                    }

                let frame = invocation current
                let! response = io.Exchange frame token

                match page response with
                | Some(Outcome.Succeeded result) ->
                    rows io prepared frame response result

                    if prepared.All && result.NextCursor.IsSome then
                        if result.NextCursor = cursor then
                            invalidOp "A case page continuation must advance."

                        cursor <- result.NextCursor
                    else
                        if prepared.Format = VerbFormat.Csv then
                            TextReplyFormat.nextLines prepared.Language result.NextCursor
                            |> text
                            |> io.Errors

                        running <- false
                | _ ->
                    failure io prepared cursor frame response
                    code <- response.ExitCode
                    running <- false

            return
                if token.IsCancellationRequested then
                    ExitCode.value ExitCode.Cancelled
                else
                    code
        }
