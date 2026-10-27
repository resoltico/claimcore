namespace ClaimCore.Domain

open System

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
                false
            else
                let parsed = Uri(value, UriKind.Absolute)

                parsed.Scheme = Uri.UriSchemeHttps
                && parsed.UserInfo = ""
                && parsed.Query = ""
                && parsed.Fragment = ""
                && String.Equals(parsed.AbsoluteUri, value, StringComparison.Ordinal)
        with :? UriFormatException ->
            false

    let private identifier maximum value =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= maximum
        && UnicodeText.isPersisted value

    let admit kind issuerValue subject =
        let issue target value =
            Refusal.InvalidInput(
                target,
                if String.IsNullOrWhiteSpace value then
                    Violation.TextRequired
                else
                    Violation.PrincipalFormat
            )

        let maximum = if kind = PrincipalKinds.Human then 512 else 256

        match issuer issuerValue, identifier maximum subject with
        | false, _ -> Error(issue InputTarget.PrincipalIssuer issuerValue)
        | _, false -> Error(issue InputTarget.PrincipalSubject subject)
        | true, true when kind = PrincipalKinds.Human -> Ok(HumanPrincipal(issuerValue, subject))
        | true, true when kind = PrincipalKinds.Service ->
            Ok(ServicePrincipal(issuerValue, subject))
        | _ -> Error(issue InputTarget.PrincipalSubject kind)

    let human issuerValue subject =
        admit PrincipalKinds.Human issuerValue subject

    let service issuerValue clientId =
        admit PrincipalKinds.Service issuerValue clientId

    /// Private storage projection; never infer identity from email, display name or groups.
    let storageParts =
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

    let fromStorage kind issuerValue stableValue =
        match Vocabulary.tryParse PrincipalKinds.vocabulary kind with
        | Some PrincipalKind.Human -> human issuerValue stableValue
        | Some PrincipalKind.Service -> service issuerValue stableValue
        | _ -> Error(Refusal.InvalidInput(InputTarget.PrincipalSubject, Violation.PrincipalFormat))

    let isHuman =
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

[<RequireQualifiedAccess>]
type ResourceScope =
    | Installation
    | Case of Guid

[<RequireQualifiedAccess>]
type CaseVisibility =
    | Every
    | Assigned
