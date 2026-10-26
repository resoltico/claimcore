module internal ClaimCore.Tests.CliReplies

open ClaimCore.Cli
open ClaimCore.Contract
open Expecto

let ofValue (endpoint: Endpoint<'Input, 'Output>) (value: 'Output) =
    ServiceReply.read (Route.describe endpoint) (endpoint.Output.Write value)
    |> Result.defaultWith (fun _ -> failtest "A typed reply fixture must admit completely.")
