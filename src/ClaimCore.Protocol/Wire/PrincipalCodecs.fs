namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module internal PrincipalCodecs =
    open Codec

    let key =
        let maximumIssuer = PrincipalKey.MaximumIssuerCharacters

        let issuerSchema =
            Schema.String(
                Some("^" + PrincipalKey.issuerScheme + "://[^/?#@\\s]+/[^?#\\s]*$"),
                None,
                Some 1,
                Some maximumIssuer
            )

        let issuer =
            boundedText 1 maximumIssuer |> constrained issuerSchema PrincipalKey.isIssuer

        let fields kind maximumSubject =
            record (fun issuer subject ->
                {
                    PrincipalInput.Kind = kind
                    Issuer = issuer
                    Subject = subject
                })
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
        |> Codec.mapResult
            (fun path (input: PrincipalInput) ->
                PrincipalKey.ofKind input.Kind input.Issuer input.Subject
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid principal representation."
                    }))
            PrincipalKey.toInput
