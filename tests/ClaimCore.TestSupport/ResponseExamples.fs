module ClaimCore.TestSupport.ResponseExamples

open ClaimCore.Wire
open ClaimCore.Presentation

let private suitable =
    function
    | ReplyKind.Succeeded
    | ReplyKind.Reviewed
    | ReplyKind.Accepted
    | ReplyKind.AlreadyAccepted
    | ReplyKind.NotAccepted -> true
    | ReplyKind.Rejected
    | ReplyKind.Failed
    | ReplyKind.OutcomeUnknown
    | ReplyKind.Pending -> false

/// A default is a declared, admitted, definite example; a missing positive fixture is a test defect.
let defaultReply (endpoint: Endpoint<'Input, 'Output>) =
    PositiveExamples.forEndpoint "examples/responses.json" endpoint.Token
    |> List.map (fun value ->
        endpoint.Output.Read "$" value
        |> Result.defaultWith (fun problem -> invalidOp (endpoint.Token + ": " + string problem)))
    |> List.tryFind (fun value -> suitable (endpoint.Classify value).Kind)
    |> Option.defaultWith (fun () ->
        invalidOp ("Missing successful response example: " + endpoint.Token))
