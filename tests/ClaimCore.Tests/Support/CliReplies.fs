module internal ClaimCore.Tests.CliReplies

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Cli
open Expecto

let ofValue (endpoint: Endpoint<'Input, 'Output>) (value: 'Output) =
    ServiceReply.read (Route.describe endpoint) (endpoint.Output.Write value)
    |> Result.defaultWith (fun _ -> failtest "A typed reply fixture must admit completely.")

let endpointId name =
    Api.tryFind name
    |> Option.map _.Id
    |> Option.defaultWith (fun () -> failtestf "Unknown fixture endpoint: %s" name)

// Compare transport facts and exit semantics; endpoint closures are deliberately not comparable.
let frameFacts frame =
    Frames.encode frame, Frames.exitCode frame

let ofData endpoint reply =
    ofValue endpoint (RepresentationReply.Data reply)

let humanPreview input reply =
    ofValue Api.operationPreview (RepresentationReply.Human(HumanReplies.preview input reply))

let humanExecute reply =
    ofValue Api.operationExecute (RepresentationReply.Human(HumanReplies.execute reply))

let humanLookup reply =
    ofValue Api.operationGet (RepresentationReply.Human(HumanReplies.lookup reply))

let writeData (endpoint: Endpoint<'input, RepresentationReply<'human, 'data>>) reply =
    endpoint.Output.Write(RepresentationReply.Data reply)

let encodeData (endpoint: Endpoint<'input, RepresentationReply<'human, 'data>>) reply =
    ClaimCore.Json.Codec.encode endpoint.Output (RepresentationReply.Data reply)
