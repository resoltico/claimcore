namespace ClaimCore.Domain

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type ContactText = private ContactText of PartyName

module ContactText =
    let descriptor = PartyName.descriptor
    let format (ContactText value) = PartyName.format value

    let parse value =
        PartyName.parse value |> Result.map ContactText

    let admit (ContactText value) =
        PartyName.admit value |> Result.map ContactText

[<RequireQualifiedAccess>]
type ActorProfileField =
    | DisplayName
    | Contact

type ActorProfileInput =
    {
        DisplayName: string
        Contact: string option
    }

type ActorProfile =
    {
        DisplayName: PartyName
        Contact: ContactText option
    }

module ActorProfile =
    let private parseWith name contact (input: ActorProfileInput) =
        let displayName = PartyName.parse input.DisplayName |> Result.bind name

        let contact =
            match input.Contact with
            | None -> Ok None
            | Some raw -> ContactText.parse raw |> Result.bind contact |> Result.map Some

        let issues =
            [
                match displayName with
                | Error issue -> yield ActorProfileField.DisplayName, issue
                | Ok _ -> ()
                match contact with
                | Error issue -> yield ActorProfileField.Contact, issue
                | Ok _ -> ()
            ]

        match displayName, contact with
        | Ok displayName, Ok contact ->
            Ok
                {
                    ActorProfile.DisplayName = displayName
                    Contact = contact
                }
        | _ -> Error issues

    let parse input =
        parseWith PartyName.admit ContactText.admit input

    /// Retained value parsing never re-applies current authoring admission.
    let restore input = parseWith Ok Ok input

    let authored (profile: ActorProfile) : ActorProfileInput =
        {
            DisplayName = PartyName.format profile.DisplayName
            Contact = profile.Contact |> Option.map ContactText.format
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
