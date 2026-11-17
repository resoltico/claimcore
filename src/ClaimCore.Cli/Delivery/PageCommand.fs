namespace ClaimCore.Cli

open System.Text
open System.Threading
open System.Collections.Generic
open ClaimCore.CommandLine
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

/// All declared paged endpoints share continuation, rendering, cancellation and failure handling.
module internal PageCommand =
    let private text =
        function
        | [] -> Array.empty<byte>
        | lines -> String.concat "\n" lines + "\n" |> Encoding.UTF8.GetBytes

    let private cursor value =
        match JsonValue.tryMember "nextCursor" value with
        | Some JsonValue.Null -> None
        | Some(JsonValue.String value) -> Some value
        | _ -> invalidOp "A successful paged DATA reply requires a continuation member."

    let runWith options (io: ReadPageIO) (prepared: PreparedVerb) (token: CancellationToken) =
        task {
            let initial = prepared.Invocation

            let declaration =
                EndpointDeclarations.find (Frames.endpoint initial).Token
                |> Option.defaultWith (fun () ->
                    invalidArg (nameof prepared) "The endpoint is not declared.")

            if not declaration.Paged then
                invalidArg (nameof prepared) "A paged endpoint is required."

            let mutable frame =
                initial
                |> Frames.withRepresentation (
                    if prepared.Format = VerbFormat.Text then
                        Representation.Human
                    else
                        Representation.Data
                )

            let mutable current =
                match JsonValue.tryMember WireMembers.Cursor (Frames.input frame) with
                | Some(JsonValue.String value) -> Some value
                | _ -> None

            let seen = HashSet<string>()
            current |> Option.iter (fun value -> seen.Add value |> ignore)
            let mutable running = true
            let mutable includeHeader = true
            let mutable code = ExitCode.value ExitCode.Success

            if prepared.Format = VerbFormat.Csv then
                EndpointValues.csvHeader (Frames.typedInput frame)
                |> Option.defaultWith (fun () ->
                    invalidOp "A declared CSV endpoint must provide a header.")
                |> io.Output

            while running && not token.IsCancellationRequested do
                let! response = io.Exchange frame token

                let page =
                    match response.Frame.Result with
                    | FrameResult.Reply value when
                        value.Route.Id = (Frames.endpoint frame).Id
                        && value.Class = ReplyKind.Succeeded
                        ->
                        match EndpointValues.humanRead value.Typed with
                        | Some {
                                   Outcome = HumanReadOutcome.Succeeded document
                               } ->
                            ViewText.renderDocumentWithHeader
                                options
                                includeHeader
                                prepared.Details
                                prepared.Language
                                document
                            |> text
                            |> io.Output

                            Some document.NextCursor
                        | Some _ -> None
                        | None ->
                            if prepared.Format = VerbFormat.Csv then
                                EndpointValues.csvRows
                                    (MessageCatalogs.render LocalizationPolicy.defaultLanguage)
                                    value.Typed
                                |> Option.defaultWith (fun () ->
                                    invalidOp "A declared CSV page must provide typed DATA rows.")
                                |> io.Output
                            else
                                io.Deliver frame response

                            let data =
                                JsonValue.tryMember "outcome" value.Wire
                                |> Option.bind (JsonValue.tryMember "data")
                                |> Option.defaultWith (fun () ->
                                    invalidOp "A paged DATA response requires a successful value.")

                            Some(cursor data)
                    | FrameResult.Reply value when value.Route.Id <> (Frames.endpoint frame).Id ->
                        invalidOp "A page must retain its requested endpoint."
                    | _ -> None

                match page with
                | Some next ->
                    includeHeader <- false

                    if prepared.All && next.IsSome then
                        if not (seen.Add next.Value) then
                            invalidOp "A page continuation must advance."

                        current <- next

                        frame <-
                            Frames.withCursor next frame
                            |> Result.defaultWith (fun _ ->
                                invalidOp "A continuation must preserve request admission.")
                    else
                        if prepared.Format = VerbFormat.Csv then
                            ReplyTextFormat.nextLines prepared.Language next |> text |> io.Errors

                        running <- false
                | None ->
                    if prepared.Format = VerbFormat.Csv then
                        ReplyText.bytesFor prepared.Language (Some frame) response.Frame
                        |> io.Errors
                    else
                        io.Deliver frame response

                    MessageCatalogs.renderCli
                        prepared.Language
                        (CliMessage.CursorReached(defaultArg current "—"))
                    |> fun value -> text [ value ] |> io.Errors

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
