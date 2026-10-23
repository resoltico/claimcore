namespace ClaimCore.Application

open ClaimCore.Contract

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Identity is the exact configured issuer plus its immutable subject or client ID.
/// Email, display name, groups and a database login are deliberately absent.
[<StructuralEquality; StructuralComparison>]
type PrincipalKey =
    private
    | HumanPrincipal of issuer: string * subject: string
    | ServicePrincipal of issuer: string * clientId: string

module PrincipalKey =
    let private issuer value =
        try
            if
                String.IsNullOrWhiteSpace value
                || value.Length > 2048
                || not (UnicodeText.isPersisted value)
            then
                Error "ACTOR_ISSUER_INVALID"
            else
                let parsed = Uri(value, UriKind.Absolute)

                if
                    parsed.Scheme = Uri.UriSchemeHttps
                    && parsed.UserInfo = ""
                    && parsed.Query = ""
                    && parsed.Fragment = ""
                    && String.Equals(parsed.AbsoluteUri, value, StringComparison.Ordinal)
                then
                    Ok value
                else
                    Error "ACTOR_ISSUER_INVALID"
        with :? UriFormatException ->
            Error "ACTOR_ISSUER_INVALID"

    let private identifier maximum value =
        if
            not (String.IsNullOrWhiteSpace value)
            && value.Length <= maximum
            && UnicodeText.isPersisted value
        then
            Ok value
        else
            Error "ACTOR_IDENTITY_INVALID"

    let admit kind issuerValue subject =
        let issue target value =
            {
                Target = target
                Issue =
                    if String.IsNullOrWhiteSpace value then
                        InputIssue.Missing
                    else
                        InputIssue.Invalid
            }

        let maximum = if kind = PrincipalKinds.Human then 512 else 256

        match issuer issuerValue, identifier maximum subject with
        | Error _, _ -> Error(issue PrincipalInputTarget.TargetIssuer issuerValue)
        | _, Error _ -> Error(issue PrincipalInputTarget.TargetSubject subject)
        | Ok validIssuer, Ok validSubject when kind = PrincipalKinds.Human ->
            Ok(HumanPrincipal(validIssuer, validSubject))
        | Ok validIssuer, Ok validSubject when kind = PrincipalKinds.Service ->
            Ok(ServicePrincipal(validIssuer, validSubject))
        | _ -> Error(issue PrincipalInputTarget.TargetKind kind)

    let human issuerValue subject =
        admit PrincipalKinds.Human issuerValue subject
        |> Result.mapError (fun _ -> "ACTOR_PRINCIPAL_INVALID")

    let service issuerValue clientId =
        admit PrincipalKinds.Service issuerValue clientId
        |> Result.mapError (fun _ -> "ACTOR_PRINCIPAL_INVALID")

    /// Private storage projection; never infer identity from email, display name or groups.
    let internal storageParts =
        function
        | HumanPrincipal(issuerValue, subject) -> PrincipalKinds.Human, issuerValue, subject
        | ServicePrincipal(issuerValue, clientId) -> PrincipalKinds.Service, issuerValue, clientId

    let describe principal : Principal =
        let kind, issuer, subject = storageParts principal

        {
            Kind = kind
            Issuer = issuer
            Subject = subject
        }

    let internal fromStorage kind issuerValue stableValue =
        match kind with
        | PrincipalKinds.Human -> human issuerValue stableValue
        | PrincipalKinds.Service -> service issuerValue stableValue
        | _ -> Error "ACTOR_PRINCIPAL_INVALID"

    let internal isHuman =
        function
        | HumanPrincipal _ -> true
        | ServicePrincipal _ -> false


[<RequireQualifiedAccess>]
type GrantScope =
    | Installation
    | Case of Guid

type ActorGrant = { Role: Role; Scope: GrantScope }

type ActorAuthority =
    {
        ActorId: Guid
        Principal: PrincipalKey
        Enabled: bool
        Grants: ActorGrant list
    }

type Actor =
    {
        ActorId: Guid
        Principal: PrincipalKey
        Enabled: bool
    }

/// Only roles that can influence this decision are loaded. Operation identity may add a second case.
type AuthorityView =
    {
        Actor: Actor option
        Installation: Role list
        Cases: Map<Guid, Role list>
        HasAssignedCases: bool
    }

module AuthorityView =
    let empty =
        {
            Actor = None
            Installation = []
            Cases = Map.empty
            HasAssignedCases = false
        }

    let ofActor (actor: ActorAuthority option) =
        match actor with
        | None -> empty
        | Some actor ->
            let installation =
                actor.Grants
                |> List.choose (fun grant ->
                    if grant.Scope = GrantScope.Installation then
                        Some grant.Role
                    else
                        None)

            let cases =
                actor.Grants
                |> List.choose (fun grant ->
                    match grant.Scope with
                    | GrantScope.Case id -> Some(id, grant.Role)
                    | _ -> None)
                |> List.groupBy fst
                |> List.map (fun (id, grants) -> id, grants |> List.map snd)
                |> Map.ofList

            {
                Actor =
                    Some
                        {
                            ActorId = actor.ActorId
                            Principal = actor.Principal
                            Enabled = actor.Enabled
                        }
                Installation = installation
                Cases = cases
                HasAssignedCases = false
            }
