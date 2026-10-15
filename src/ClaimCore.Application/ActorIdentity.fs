namespace ClaimCore.Application

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

    let human issuerValue subject =
        match issuer issuerValue, identifier 512 subject with
        | Ok validIssuer, Ok validSubject -> Ok(HumanPrincipal(validIssuer, validSubject))
        | _ -> Error "ACTOR_PRINCIPAL_INVALID"

    let service issuerValue clientId =
        match issuer issuerValue, identifier 256 clientId with
        | Ok validIssuer, Ok validClient -> Ok(ServicePrincipal(validIssuer, validClient))
        | _ -> Error "ACTOR_PRINCIPAL_INVALID"

    /// Private storage projection; never infer identity from email, display name or groups.
    let internal storageParts =
        function
        | HumanPrincipal(issuerValue, subject) -> "HUMAN", issuerValue, subject
        | ServicePrincipal(issuerValue, clientId) -> "SERVICE", issuerValue, clientId

    let describe principal : RecordedPrincipal =
        let kind, issuer, subject = storageParts principal

        {
            Kind = kind
            Issuer = issuer
            Subject = subject
        }

    let internal fromStorage kind issuerValue stableValue =
        match kind with
        | "HUMAN" -> human issuerValue stableValue
        | "SERVICE" -> service issuerValue stableValue
        | _ -> Error "ACTOR_PRINCIPAL_INVALID"

    let internal isHuman =
        function
        | HumanPrincipal _ -> true
        | ServicePrincipal _ -> false

[<RequireQualifiedAccess>]
type Role =
    | Owner
    | CaseReader
    | CaseRegistrar
    | CaseHandler
    | PaymentRecorder
    | DataSteward
    | Auditor

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

[<RequireQualifiedAccess>]
type GrantTarget =
    | Installation
    | CaseReference of string
