namespace ClaimCore.Domain

open System
open System.Text

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type ContactText = private ContactText of PartyName

module ContactText =
    let descriptor = PartyName.descriptor
    let format (ContactText value) = PartyName.format value

    let parse value =
        PartyName.parse value |> Result.map ContactText

    let admit (ContactText value) =
        PartyName.admit value |> Result.map ContactText

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type EmailAddress = private EmailAddress of string

module EmailAddress =
    [<Literal>]
    let MaximumCharacters = 254

    [<Literal>]
    let MaximumLocalCharacters = 64

    let descriptor = ScalarText.descriptor None (Some MaximumCharacters)
    let format (EmailAddress value) = value

    let parse (value: string) =
        let invalid = Error Violation.EmailAddressInvalid

        if not (UnicodeText.isPersisted value) then
            invalid
        else
            let count (text: string) = text.EnumerateRunes() |> Seq.length

            match value.Split '@' with
            | [| local; domain |] when
                count local >= 1
                && count local <= MaximumLocalCharacters
                && count value <= MaximumCharacters
                && domain.Contains '.'
                && not (
                    value.EnumerateRunes()
                    |> Seq.exists (fun rune -> Rune.IsWhiteSpace rune || Rune.IsControl rune)
                )
                ->
                Ok(EmailAddress value)
            | _ -> invalid

[<RequireQualifiedAccess>]
type ActorProfileField =
    | DisplayName
    | Contact
    | DigestEmail

type ActorProfileInput =
    {
        DisplayName: string
        Contact: string option
        DigestEmail: string option
    }

type ActorProfile =
    {
        DisplayName: PartyName
        Contact: ContactText option
        DigestEmail: EmailAddress option
    }

module ActorProfile =
    let private combine displayName contact digestEmail =
        let issues =
            [
                match displayName with
                | Error issue -> yield ActorProfileField.DisplayName, issue
                | Ok _ -> ()
                match contact with
                | Error issue -> yield ActorProfileField.Contact, issue
                | Ok _ -> ()
                match digestEmail with
                | Error issue -> yield ActorProfileField.DigestEmail, issue
                | Ok _ -> ()
            ]

        match displayName, contact, digestEmail with
        | Ok displayName, Ok contact, Ok digestEmail ->
            Ok
                {
                    ActorProfile.DisplayName = displayName
                    Contact = contact
                    DigestEmail = digestEmail
                }
        | _ -> Error issues

    let private parseWith name contact (input: ActorProfileInput) =
        combine
            (PartyName.parse input.DisplayName |> Result.bind name)
            (match input.Contact with
             | None -> Ok None
             | Some raw -> ContactText.parse raw |> Result.bind contact |> Result.map Some)
            (match input.DigestEmail with
             | None -> Ok None
             | Some raw -> EmailAddress.parse raw |> Result.map Some)

    /// Current authoring policy consumes already parsed profile values.
    let admit (profile: ActorProfile) =
        combine
            (PartyName.admit profile.DisplayName)
            (match profile.Contact with
             | None -> Ok None
             | Some value -> ContactText.admit value |> Result.map Some)
            (Ok profile.DigestEmail)

    let parse input =
        parseWith PartyName.admit ContactText.admit input

    /// Retained value parsing never re-applies current authoring admission.
    let restore input = parseWith Ok Ok input

    let authored (profile: ActorProfile) : ActorProfileInput =
        {
            DisplayName = PartyName.format profile.DisplayName
            Contact = profile.Contact |> Option.map ContactText.format
            DigestEmail = profile.DigestEmail |> Option.map EmailAddress.format
        }

[<RequireQualifiedAccess>]
type DisplayName =
    | Named of PartyName
    | Erased

[<RequireQualifiedAccess>]
type ActorAvailability =
    | Available
    | Disabled
    | NotEligible

module ActorAvailability =
    let vocabulary =
        Vocabulary.ofUnion<ActorAvailability> "actorAvailability" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

type ActorRef =
    {
        ActorId: ActorId
        DisplayName: DisplayName
        PrincipalKind: PrincipalKind
        Availability: ActorAvailability
    }

module ActorRef =
    let create actorId principalKind availability profile =
        {
            ActorId = actorId
            DisplayName =
                profile
                |> Option.map (fun (profile: ActorProfile) -> DisplayName.Named profile.DisplayName)
                |> Option.defaultValue DisplayName.Erased
            PrincipalKind = principalKind
            Availability = availability
        }

/// The current account label is available only for person-attributed authority.
[<RequireQualifiedAccess>]
type AuthorityRef =
    | Actor of ActorRef
    | Operator

module AuthorityRef =
    let actor =
        function
        | AuthorityRef.Actor actor -> Some actor
        | AuthorityRef.Operator -> None
