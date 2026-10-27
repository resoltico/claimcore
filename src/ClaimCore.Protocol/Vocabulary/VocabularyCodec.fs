namespace ClaimCore.Protocol

open ClaimCore.Protocol

open ClaimCore.Domain

open ClaimCore.Json

/// A finite product vocabulary supplies its spelling, ordering and union coverage.
module VocabularyCodec =
    let create values =
        Codec.enumeration (Vocabulary.entries values)
