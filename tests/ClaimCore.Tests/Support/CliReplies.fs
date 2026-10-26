module internal ClaimCore.Tests.CliReplies

open ClaimCore.Cli
open ClaimCore.Contract
open Expecto

let ofValue (endpoint: Endpoint<'Input, 'Output>) (value: 'Output) =
    ServiceReply.read (Route.describe endpoint) (endpoint.Output.Write value)
    |> Result.defaultWith (fun _ -> failtest "A typed reply fixture must admit completely.")

let endpointId name =
    Api.tryFind name
    |> Option.map _.Id
    |> Option.defaultWith (fun () -> failtestf "Unknown fixture endpoint: %s" name)

let correlates endpoint input output =
    ServiceReply.read endpoint output
    |> Result.exists (ServiceReply.correlates input)
