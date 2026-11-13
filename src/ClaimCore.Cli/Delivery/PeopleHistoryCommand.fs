namespace ClaimCore.Cli

open ClaimCore.CommandLine

open System.Text
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

module internal PeopleHistoryCommand =
    let run (io: ReadPageIO) (prepared: PreparedVerb) token =
        task {
            let frame = Frames.withRepresentation Representation.Data prepared.Invocation

            let! reply = io.Exchange frame token

            let page =
                match reply.Frame.Result with
                | FrameResult.Reply value when value.Route.Id = Api.peopleHistory.Id ->
                    ServiceReply.value Api.peopleHistory value
                | _ -> None

            match page with
            | Some(RepresentationReply.Data(Outcome.Succeeded page)) ->
                io.Output PeopleHistoryCsv.csvHeader
                io.Output(PeopleHistoryCsv.csvRows page.Entries)
                let next = ReplyTextFormat.nextLines prepared.Language page.NextCursor

                if not next.IsEmpty then
                    io.Errors(Encoding.UTF8.GetBytes(String.concat "\n" next + "\n"))
            | _ -> io.Errors(ReplyText.bytesFor prepared.Language (Some frame) reply.Frame)

            return
                if token.IsCancellationRequested then
                    ExitCode.value ExitCode.Cancelled
                else
                    reply.ExitCode
        }
