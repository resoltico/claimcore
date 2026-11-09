namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module internal PrincipalCodecs =
    open Codec

    let issuer =
        let maximumIssuer = PrincipalKey.MaximumIssuerCharacters

        // Use the same Unicode categories in both regex engines, rather than their different shorthand classes.
        let nonUriCharacters = @"\p{Cc}\p{Zs}\p{Zl}\p{Zp}\uFEFF"

        let pattern =
            "^"
            + PrincipalKey.issuerScheme
            + "://[^/?#@"
            + nonUriCharacters
            + "]+/[^?#"
            + nonUriCharacters
            + "]*(?![\\s\\S])"

        patternedText 1 maximumIssuer pattern None |> refine PrincipalKey.isIssuer

    let key =
        let fields kind maximumSubject =
            (record (fun issuer subject ->
                {
                    PrincipalInput.Kind = kind
                    Issuer = issuer
                    Subject = subject
                })
            : Fields<PrincipalInput, _>)
            |> field "issuer" _.Issuer issuer
            |> field "subject" _.Subject (boundedText 1 maximumSubject)

        let alternative kind maximumSubject =
            variant
                (Vocabulary.token PrincipalKind.vocabulary kind)
                id
                (fun (input: PrincipalInput) -> if input.Kind = kind then Some input else None)
                (fields kind maximumSubject)

        tagged
            "Principal"
            [
                alternative PrincipalKind.Human PrincipalKey.MaximumHumanSubjectCharacters
                alternative PrincipalKind.Service PrincipalKey.MaximumServiceSubjectCharacters
            ]
        |> Codec.interpret
            (fun path (input: PrincipalInput) ->
                PrincipalKey.ofKind input.Kind input.Issuer input.Subject
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid principal representation."
                    }))
            PrincipalKey.toInput
