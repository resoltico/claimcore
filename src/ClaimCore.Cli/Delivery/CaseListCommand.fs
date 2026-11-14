namespace ClaimCore.Cli

open ClaimCore.Wire.Export

open ClaimCore.CommandLine

open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
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
        | VerbFormat.Csv -> io.Output CaseCsv.csvHeader
        | VerbFormat.Text -> ()
        | VerbFormat.Json -> ()

    let private documentRows
        includeHeader
        options
        (io: ReadPageIO)
        (prepared: PreparedVerb)
        (document: HumanDocument)
        =
        let render =
            if includeHeader then
                ViewText.renderWithDetails prepared.Details
            else
                ViewText.renderRowsWithDetails prepared.Details

        io.Output(text (render options prepared.Language document.Screen))

    let private failure (io: ReadPageIO) (prepared: PreparedVerb) cursor frame response =
        if prepared.Format = VerbFormat.Csv then
            let failureText =
                match response.Frame.Result with
                | FrameResult.Reply reply ->
                    match ServiceReply.value Api.caseList reply with
                    | Some(RepresentationReply.Data outcome) ->
                        DiagnosticText.readLinesFor
                            prepared.Language
                            None
                            (fun _ -> invalidOp "Successful CSV pages must be emitted as rows.")
                            outcome
                        |> text
                    | _ -> ReplyText.bytesFor prepared.Language (Some frame) response.Frame
                | _ -> ReplyText.bytesFor prepared.Language (Some frame) response.Frame

            io.Errors failureText
        else
            io.Deliver frame response

        let reached = defaultArg cursor "—"

        MessageCatalogs.renderCli prepared.Language (CliMessage.CursorReached reached)
        |> fun value -> io.Errors(text [ value ])

    let runWith options (io: ReadPageIO) (prepared: PreparedVerb) (token: CancellationToken) =
        task {
            let initial = prepared.Invocation

            let request =
                Frames.value Api.caseList initial
                |> Option.defaultWith (fun () ->
                    invalidOp "A listing must carry a case-list request.")

            let mutable cursor = request.Page.AfterCursor
            let mutable running = true
            let mutable includeHeader = true
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

                let frame =
                    invocation current
                    |> Frames.withRepresentation (
                        if prepared.Format = VerbFormat.Text then
                            Representation.Human
                        else
                            Representation.Data
                    )

                let! response = io.Exchange frame token

                let next =
                    match page response with
                    | Some(RepresentationReply.Data(Outcome.Succeeded result)) ->
                        if prepared.Format = VerbFormat.Csv then
                            io.Output(CaseCsv.csvRows result.Basis result.Items)
                        else
                            io.Deliver frame response

                        Some result.NextCursor
                    | Some(RepresentationReply.Human {
                                                         Outcome = HumanReadOutcome.Succeeded document
                                                     }) ->
                        documentRows includeHeader options io prepared document
                        Some(document.NextCursor)
                    | _ -> None

                match next with
                | Some next ->
                    includeHeader <- false

                    if prepared.All && next.IsSome then
                        if next = cursor then
                            invalidOp "A case page continuation must advance."

                        cursor <- next
                    else
                        if prepared.Format = VerbFormat.Csv then
                            ReplyTextFormat.nextLines prepared.Language next |> text |> io.Errors

                        running <- false
                | None ->
                    failure io prepared cursor frame response
                    code <- response.ExitCode
                    running <- false

            return
                if token.IsCancellationRequested then
                    ExitCode.value ExitCode.Cancelled
                else
                    code
        }

    let run io prepared token =
        runWith (TextTable.environment ()) io prepared token
