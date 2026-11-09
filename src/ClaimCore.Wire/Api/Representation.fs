namespace ClaimCore.Wire

open System
open ClaimCore.Domain
open ClaimCore.Json

[<RequireQualifiedAccess>]
type Representation =
    | Human
    | Data

module Representation =
    [<Literal>]
    let HeaderName = "ClaimCore-Representation"

    let vocabulary =
        Vocabulary.ofUnion<Representation> "representation" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

    /// HTTP field values are case-insensitive; conflicting or unsupported selections never fall back.
    let select header =
        match header with
        | None -> Ok Representation.Data
        | Some values ->
            let selected =
                values
                |> List.collect (fun (value: string) -> value.Split(',') |> Array.toList)
                |> List.map (fun value ->
                    value.Trim(' ', '\t').ToUpperInvariant() |> Vocabulary.tryParse vocabulary)

            match selected |> List.distinct with
            | [ Some representation ] -> Ok representation
            | _ -> Error()

/// The response representation cannot combine a HUMAN document with a DATA projection.
[<RequireQualifiedAccess>]
type RepresentationReply<'human, 'data> =
    | Human of 'human
    | Data of 'data

module RepresentationReply =
    let representation =
        function
        | RepresentationReply.Human _ -> Representation.Human
        | RepresentationReply.Data _ -> Representation.Data

    let matches expected reply = representation reply = expected

module RepresentationCodecs =
    let reply name human data =
        Codec.choice
            name
            [
                Codec.branch
                    RepresentationReply.Human
                    (function
                    | RepresentationReply.Human value -> Some value
                    | RepresentationReply.Data _ -> None)
                    (human
                     |> Codec.withLiteral
                         "representation"
                         (Representation.token Representation.Human)
                     |> Codec.named (name + ".HUMAN"))
                Codec.branch
                    RepresentationReply.Data
                    (function
                    | RepresentationReply.Data value -> Some value
                    | RepresentationReply.Human _ -> None)
                    (data
                     |> Codec.withLiteral
                         "representation"
                         (Representation.token Representation.Data)
                     |> Codec.named (name + ".DATA"))
            ]
