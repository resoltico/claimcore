namespace ClaimCore.Wire

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Presentation

module internal PresentationKeys =
    open Codec
    open ResponseScalars

    let MaximumKeyCharacters = 256

    let key = boundedText 1 MaximumKeyCharacters

    let messageId =
        let pattern = "^[A-Za-z][A-Za-z0-9_.]*(?![\\s\\S])"

        patternedText 1 MaximumKeyCharacters pattern None
