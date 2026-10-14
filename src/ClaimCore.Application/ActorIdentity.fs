namespace ClaimCore.Application

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
            if String.IsNullOrWhiteSpace value || value.Length > 2048 then
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
        if not (String.IsNullOrWhiteSpace value) && value.Length <= maximum then
            try
                Text.UTF8Encoding(false, true).GetByteCount(value) |> ignore

                if value.Contains('\u0000') then
                    Error "ACTOR_IDENTITY_INVALID"
                else
                    Ok value
            with :? Text.EncoderFallbackException ->
                Error "ACTOR_IDENTITY_INVALID"
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
    | CaseEditor
    | DataSteward
    | Auditor

module Roles =
    let all =
        [ Role.Owner; Role.CaseReader; Role.CaseEditor; Role.DataSteward; Role.Auditor ]

    let token =
        function
        | Role.Owner -> "OWNER"
        | Role.CaseReader -> "CASE_READER"
        | Role.CaseEditor -> "CASE_EDITOR"
        | Role.DataSteward -> "DATA_STEWARD"
        | Role.Auditor -> "AUDITOR"

    let tryParse value =
        all |> List.tryFind (token >> (=) value)

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
