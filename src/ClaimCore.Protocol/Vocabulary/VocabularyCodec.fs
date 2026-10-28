namespace ClaimCore.Protocol

open ClaimCore.Protocol

open ClaimCore.Domain

open ClaimCore.Json

/// A finite product vocabulary supplies its spelling, ordering and union coverage.
module VocabularyCodec =
    let select accepts values =
        Vocabulary.entries values
        |> List.filter (fst >> accepts)
        |> Codec.enumeration
        |> Codec.vocabulary (Vocabulary.name values)

    let create values = select (fun _ -> true) values
