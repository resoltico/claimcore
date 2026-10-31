namespace ClaimCore.Cli

open System
open System.IO
open System.Threading
open ClaimCore.Domain
open ClaimCore.Protocol

[<NoEquality; NoComparison>]
type internal VerbConsole =
    {
        Output: Stream
        Errors: Stream
        Review: InteractiveConsole
    }

/// Ordinary verbs and the shell share preparation, review, pagination and delivery.
module internal VerbRunner =
    let private render (prepared: PreparedVerb) frame (reply: CliReply) =
        if prepared.Format = VerbFormat.Json then
            reply.Bytes
        else
            TextReply.bytesFor prepared.Language (Some frame) reply.Frame

    let private pages (io: VerbConsole) (processor: RemoteFrameProcessor) prepared token =
        let pageIO =
            {
                Exchange = fun frame token -> processor.Exchange(frame, token)
                Deliver = fun frame reply -> processor.Deliver(render prepared frame, reply)
                Output =
                    fun bytes ->
                        io.Output.Write(bytes: byte array)
                        io.Output.Flush()
                Errors =
                    fun bytes ->
                        io.Errors.Write(bytes: byte array)
                        io.Errors.Flush()
            }

        let frame = prepared.Invocation

        if (Frames.endpoint frame).Id = Api.authorityHistory.Id then
            AuthorityHistoryCommand.run pageIO prepared token
        else
            CaseListCommand.run pageIO prepared token

    let private prepared (io: VerbConsole) (processor: RemoteFrameProcessor) arguments token =
        task {
            match Grammar.prepare (fun () -> Guid.NewGuid().ToString("D")) arguments with
            | Error issues ->
                CliDiscovery.writeText
                    io.Errors
                    (UsageText.render (CliInvocations.usageLanguage arguments) arguments issues)

                return ExitCode.value ExitCode.Usage
            | Ok prepared when prepared.DryRun ->
                CliDiscovery.writeBytes io.Output prepared.Bytes
                return ExitCode.value ExitCode.Success
            | Ok prepared when prepared.All || prepared.Format = VerbFormat.Csv ->
                return! pages io processor prepared token
            | Ok prepared ->
                let frame = prepared.Invocation

                let! reply = processor.Exchange(frame, token)
                processor.Deliver(render prepared frame, reply)
                return reply.ExitCode
        }

    let run io (processor: RemoteFrameProcessor) arguments token =
        task {
            match InteractiveReview.prepare io.Review arguments with
            | Error issues ->
                CliDiscovery.writeText
                    io.Errors
                    (UsageText.render (CliInvocations.usageLanguage arguments) arguments issues)

                return ExitCode.value ExitCode.Usage
            | Ok None -> return! prepared io processor arguments token
            | Ok(Some draft) ->
                let transport =
                    {
                        Exchange = fun frame token -> processor.Exchange(frame, token)
                        Deliver =
                            fun frame reply ->
                                processor.Deliver(
                                    (fun response ->
                                        TextReply.bytesFor
                                            draft.Language
                                            (Some frame)
                                            response.Frame),
                                    reply
                                )
                    }

                return! InteractiveReview.run Guid.NewGuid io.Review transport draft token
        }
