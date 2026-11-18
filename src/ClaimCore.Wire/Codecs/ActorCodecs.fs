namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Json

/// Actor references intentionally exclude contact and principal identity details.
module internal ActorCodecs =
    open Codec
    open ResponseScalars

    let displayName =
        tagged
            "DisplayName"
            [
                variant
                    "NAMED"
                    DisplayName.Named
                    (function
                    | DisplayName.Named value -> Some value
                    | DisplayName.Erased -> None)
                    (record id |> field "value" id partyName)
                unitVariant "ERASED" DisplayName.Erased
            ]

    let emailAddress =
        boundedUtf8Text 1 (4 * EmailAddress.MaximumCharacters)
        |> interpret
            (fun path raw ->
                EmailAddress.parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid email address."
                    }))
            EmailAddress.format

    let profile =
        let contact =
            boundedUtf8Text 1 PartyName.MaximumBytes
            |> interpret
                (fun path raw ->
                    ContactText.parse raw
                    |> Result.mapError (fun _ ->
                        {
                            Path = path
                            Reason = "Invalid profile contact."
                        }))
                ContactText.format

        (record (fun name contact email ->
            {
                ActorProfile.DisplayName = name
                Contact = contact
                DigestEmail = email
            })
        : Fields<ActorProfile, _>)
        |> field "displayName" _.DisplayName partyName
        |> field "contact" _.Contact (nullable contact)
        |> optionalField "digestEmail" _.DigestEmail emailAddress
        |> build
        |> named "ActorProfile"

    let actorRef =
        (record (fun actor name kind availability ->
            {
                ActorRef.ActorId = actor
                DisplayName = name
                PrincipalKind = kind
                Availability = availability
            })
        : Fields<ActorRef, _>)
        |> field "actorId" _.ActorId actorId
        |> field "displayName" _.DisplayName displayName
        |> field "principalKind" _.PrincipalKind (VocabularyCodec.create PrincipalKind.vocabulary)
        |> field "availability" _.Availability (VocabularyCodec.create ActorAvailability.vocabulary)
        |> build
        |> named "ActorRef"

    let authorityRef =
        tagged
            "AuthorityRef"
            [
                variant
                    "ACTOR"
                    AuthorityRef.Actor
                    (function
                    | AuthorityRef.Actor value -> Some value
                    | AuthorityRef.Operator -> None)
                    (record id |> field "actor" id actorRef)
                unitVariant "OPERATOR" AuthorityRef.Operator
            ]
