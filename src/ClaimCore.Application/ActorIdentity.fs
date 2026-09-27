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
    | RecoveryOperator
    | RecoveryExporter
    | AuditorCustodian
    | DataSteward

[<RequireQualifiedAccess>]
type GrantScope =
    | Installation
    | Case of Guid

type ActorGrant = { Role: Role; Scope: GrantScope }

/// A scoped, revisioned snapshot supplied by a trusted grant store. Constructing this value is
/// not mutation authority: commit must re-read it under the grant and case locks.
type ActorAuthority =
    {
        ActorId: Guid
        Principal: PrincipalKey
        Enabled: bool
        GrantRevision: int64
        Grants: ActorGrant list
    }

/// Exact witnessed actor/grant action; transport and storage encode this value canonically.
type internal ActorAuthorityAction =
    {
        EventId: Guid
        Revision: int64
        ActionName: string
        TargetActorId: Guid
        ApproverActorId: Guid option
        Principal: PrincipalKey option
        Grant: ActorGrant option
        Enabled: bool option
    }

/// A verified authority snapshot is carried explicitly through core and storage calls.
/// A mutation must re-read this revision under its authoritative lock before COMMIT.
type internal ActorBinding =
    {
        Principal: PrincipalKey
        ActorId: Guid
        GrantRevision: int64
    }

type internal CommandAuthority = { Actor: ActorBinding; CaseId: Guid }

[<RequireQualifiedAccess>]
type internal AttemptActorPhase =
    | NormalSubmit
    | RecoveryResolve

type internal ExecutionAttribution =
    {
        Command: CommandAuthority
        PreparerActorId: Guid
        ImporterActorId: Guid option
        Phase: AttemptActorPhase
    }

type internal RevocationActorEvidence =
    {
        CaseId: Guid
        RevokingActorId: Guid
        GrantRevision: int64
    }
