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
    [<Literal>]
    let MaximumIssuerCharacters = 2048

    [<Literal>]
    let MaximumHumanSubjectCharacters = 512

    [<Literal>]
    let MaximumServiceSubjectCharacters = 256

    let issuerScheme = Uri.UriSchemeHttps

    let isIssuer value =
        try
            if
                String.IsNullOrWhiteSpace value
                || value.Length > MaximumIssuerCharacters
                || not (UnicodeText.isPersisted value)
            then
                false
            else
                let parsed = Uri(value, UriKind.Absolute)

                parsed.Scheme = issuerScheme
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

    let ofKind kind issuerValue subject =
        let issue target value =
            Refusal.invalidInput (
                target,
                if String.IsNullOrWhiteSpace value then
                    Violation.TextRequired
                else
                    Violation.PrincipalFormat
            )

        let maximum =
            match kind with
            | PrincipalKind.Human -> MaximumHumanSubjectCharacters
            | PrincipalKind.Service -> MaximumServiceSubjectCharacters

        let validIssuer =
            if isIssuer issuerValue then
                Ok()
            else
                Error(issue InputTarget.PrincipalIssuer issuerValue)

        let validSubject =
            if identifier maximum subject then
                Ok()
            else
                Error(issue InputTarget.PrincipalSubject subject)

        Admission.map2
            (fun () () ->
                match kind with
                | PrincipalKind.Human -> HumanPrincipal(issuerValue, subject)
                | PrincipalKind.Service -> ServicePrincipal(issuerValue, subject))
            validIssuer
            validSubject

    let human issuerValue subject =
        ofKind PrincipalKind.Human issuerValue subject

    let service issuerValue clientId =
        ofKind PrincipalKind.Service issuerValue clientId

    /// Admitted identity parts; each boundary owns its token spellings.
    let parts =
        function
        | HumanPrincipal(issuerValue, subject) -> PrincipalKind.Human, issuerValue, subject
        | ServicePrincipal(issuerValue, clientId) -> PrincipalKind.Service, issuerValue, clientId

    let toInput principal : PrincipalInput =
        let kind, issuer, subject = parts principal

        {
            Kind = kind
            Issuer = issuer
            Subject = subject
        }

    let isHuman =
        function
        | HumanPrincipal _ -> true
        | ServicePrincipal _ -> false


[<RequireQualifiedAccess>]
type Scope =
    | Installation
    | Case of Guid

type ActorGrant = Grant<Scope>

type Actor =
    {
        ActorId: Guid
        Principal: PrincipalKey
        Enabled: bool
    }

type ActorAuthority =
    {
        Actor: Actor
        Grants: ActorGrant list
    }

    member this.ActorId = this.Actor.ActorId
    member this.Principal = this.Actor.Principal
    member this.Enabled = this.Actor.Enabled

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
                    if grant.Scope = Scope.Installation then
                        Some grant.Role
                    else
                        None)

            let cases =
                actor.Grants
                |> List.choose (fun grant ->
                    match grant.Scope with
                    | Scope.Case id -> Some(id, grant.Role)
                    | _ -> None)
                |> List.groupBy fst
                |> List.map (fun (id, grants) -> id, grants |> List.map snd)
                |> Map.ofList

            {
                Actor = Some actor.Actor
                Installation = installation
                Cases = cases
                HasAssignedCases = false
            }

[<RequireQualifiedAccess>]
type CaseVisibility =
    | Every
    | Assigned
