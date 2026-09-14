namespace ClaimCore.Protocol

open System.Text.Json

/// A pure, bounded codec. Successful decoding establishes shape, not authority or commitment.
[<Sealed>]
type JsonCodec<'T> internal (read: JsonElement -> 'T, write: Utf8JsonWriter -> 'T -> unit) =
    member _.Decode(maximumBytes: int, bytes: byte array) =
        JsonInput.decode maximumBytes read bytes

    member _.Encode(maximumBytes: int, value: 'T) =
        JsonWrite.encode maximumBytes read write value

[<RequireQualifiedAccess>]
type RequestBody =
    | None
    | Json
    | Raw of mediaType: string * maximumBytes: int * requiredHeaders: string list

type EndpointDescription =
    {
        Identifier: string
        Method: string
        Path: string
        Body: RequestBody
        SuccessMediaType: string option
    }

/// No transport, credentials, retries, or service activation is hidden in a binding.
[<Sealed>]
type JsonEndpoint<'Input, 'Output>
    internal
    (description: EndpointDescription, input: JsonCodec<'Input>, output: JsonCodec<'Output>) =
    member _.Description = description
    member _.Request = input
    member _.Response = output

[<Sealed>]
type ReadEndpoint<'Output> internal (description: EndpointDescription, output: JsonCodec<'Output>) =
    member _.Description = description
    member _.Response = output

/// Raw artifact bytes are not JSON DTOs; the later transport must enforce their described framing.
[<Sealed>]
type RawEndpoint<'Headers, 'Output>
    internal
    (description: EndpointDescription, headers: JsonCodec<'Headers>, output: JsonCodec<'Output>) =
    member _.Headers = headers
    member _.Description = description
    member _.Response = output
