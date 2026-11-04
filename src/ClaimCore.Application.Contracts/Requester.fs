namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

/// Optional authenticated identity-provider hints, independently bounded by current single-line admission.
type RequesterClaims =
    private
        {
            NameHint: PartyName option
            UsernameHint: PartyName option
            EmailHint: PartyName option
        }

    member this.Name = this.NameHint
    member this.PreferredUsername = this.UsernameHint
    member this.Email = this.EmailHint

module RequesterClaims =
    let admit name username email =
        let hint value =
            value
            |> Option.bind (fun raw ->
                PartyName.parse raw |> Result.bind PartyName.admit |> Result.toOption)

        {
            NameHint = hint name
            UsernameHint = hint username
            EmailHint = hint email
        }

    let empty = admit None None None

type UnregisteredRequester =
    {
        Issuer: string
        Subject: string
        Claims: RequesterClaims
    }

type RequesterWithoutRoles =
    {
        Self: ActorRef
        InstallationId: Guid
        BusinessDate: DateOnly
        TimeZone: string
    }

[<RequireQualifiedAccess>]
type RequesterKind =
    | Active
    | NoRoles
    | Disabled
    | NotRegistered

module RequesterKind =
    let vocabulary =
        Vocabulary.ofUnion<RequesterKind> "requesterState" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type RequesterState =
    | Active of ActorContext
    | NoRoles of RequesterWithoutRoles
    | Disabled of ActorRef
    | NotRegistered of UnregisteredRequester

module RequesterState =
    let kind =
        function
        | RequesterState.Active _ -> RequesterKind.Active
        | RequesterState.NoRoles _ -> RequesterKind.NoRoles
        | RequesterState.Disabled _ -> RequesterKind.Disabled
        | RequesterState.NotRegistered _ -> RequesterKind.NotRegistered

type ContextReply = { Requester: RequesterState }

type AccessRequest =
    {
        Principal: PrincipalKey
        FirstSeen: DateTimeOffset
        LastSeen: DateTimeOffset
        Claims: RequesterClaims
    }

type AccessRequestPage =
    {
        Items: AccessRequest list
        NextCursor: string option
    }
