namespace ClaimCore.Cli

open System.Text
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol

module internal AuthorityHistoryCommand =
    let run (io: ReadPageIO) (prepared: PreparedVerb) token =
        task {
            let frame = prepared.Invocation

            let! reply = io.Exchange frame token

            let page =
                match reply.Frame.Result with
                | FrameResult.Reply value when value.Route.Id = Api.authorityHistory.Id ->
                    ServiceReply.value Api.authorityHistory value
                | _ -> None

            match page with
            | Some(Outcome.Succeeded page) ->
                io.Output AuthorityHistoryRows.csvHeader
                io.Output(AuthorityHistoryRows.csvRows page.Entries)
                let next = TextReplyFormat.nextLines prepared.Language page.NextCursor

                if not next.IsEmpty then
                    io.Errors(Encoding.UTF8.GetBytes(String.concat "\n" next + "\n"))
            | _ -> io.Errors(TextReply.bytesFor prepared.Language (Some frame) reply.Frame)

            return
                if token.IsCancellationRequested then
                    ExitCode.value ExitCode.Cancelled
                else
                    reply.ExitCode
        }
