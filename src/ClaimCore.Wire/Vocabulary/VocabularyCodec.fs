namespace ClaimCore.Wire

/// Wire vocabularies use the same typed adapter as canonical business inputs.
module VocabularyCodec =
    let select accepts values =
        ClaimCore.RecordFormat.VocabularyCodec.select accepts values

    let create values =
        ClaimCore.RecordFormat.VocabularyCodec.create values
