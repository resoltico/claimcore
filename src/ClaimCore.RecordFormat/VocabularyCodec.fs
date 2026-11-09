namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// A finite vocabulary owns its tokens and ordering for both records and wire contracts.
module VocabularyCodec =
    let select accepts values =
        Vocabulary.entries values
        |> List.filter (fst >> accepts)
        |> Codec.enumeration
        |> Codec.vocabulary (Vocabulary.name values)

    let create values = select (fun _ -> true) values
