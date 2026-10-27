namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol

open System

[<RequireQualifiedAccess>]
type RemoteClientMode =
    | Interactive
    | Automation

[<NoComparison>]
type RemoteConfiguration =
    {
        Service: Uri
        Issuer: Uri
        ClientId: string
        Mode: RemoteClientMode
        SecretFile: string option
        OidcTrustRootFile: string option
        ServiceTrustRootFile: string option
    }

module RemoteConfiguration =
    let private absoluteHttpsRoot (value: string) =
        let mutable uri = Unchecked.defaultof<Uri>

        if
            Uri.TryCreate(value, UriKind.Absolute, &uri)
            && uri.Scheme = Uri.UriSchemeHttps
            && not (String.IsNullOrWhiteSpace(uri.Host))
            && String.IsNullOrEmpty(uri.UserInfo)
            && String.IsNullOrEmpty(uri.Query)
            && String.IsNullOrEmpty(uri.Fragment)
            && value = uri.AbsoluteUri
        then
            Some uri
        else
            None

    let create service issuer clientId mode secretFile =
        match absoluteHttpsRoot service, absoluteHttpsRoot issuer with
        | Some serviceUri, Some issuerUri when
            serviceUri.AbsolutePath = "/"
            && not (String.IsNullOrWhiteSpace(clientId))
            && clientId = clientId.Trim()
            && clientId.Length <= CliConfigurationLimits.MaximumClientIdCodeUnits
            ->
            match mode, secretFile with
            | RemoteClientMode.Interactive, None ->
                Ok
                    {
                        Service = serviceUri
                        Issuer = issuerUri
                        ClientId = clientId
                        Mode = mode
                        SecretFile = None
                        OidcTrustRootFile = None
                        ServiceTrustRootFile = None
                    }
            | RemoteClientMode.Automation, Some path when
                not (String.IsNullOrWhiteSpace(path)) && IO.Path.IsPathFullyQualified(path)
                ->
                Ok
                    {
                        Service = serviceUri
                        Issuer = issuerUri
                        ClientId = clientId
                        Mode = mode
                        SecretFile = Some path
                        OidcTrustRootFile = None
                        ServiceTrustRootFile = None
                    }
            | _ -> Error ClientFailure.Configuration
        | _ -> Error ClientFailure.Configuration

    let private read setting =
        let name = InputDiagnostics.settingName setting
        Environment.GetEnvironmentVariable name |> Option.ofObj

    let private invalid setting =
        Error(ClientFailure.ConfigurationIssue(setting, ConfigurationIssue.Invalid))

    let private required setting =
        match read setting with
        | None -> Error(ClientFailure.ConfigurationIssue(setting, ConfigurationIssue.Missing))
        | Some value -> Ok value

    let private root setting =
        required setting
        |> Result.bind (fun value ->
            match absoluteHttpsRoot value with
            | Some uri -> Ok uri
            | None -> invalid setting)

    let private privateRoot setting =
        match read setting with
        | None -> Ok None
        | Some path when IO.Path.IsPathFullyQualified path -> Ok(Some path)
        | _ -> invalid setting

    let private configureMode (service: Uri) (issuer: Uri) clientId =
        required CliSetting.AuthMode
        |> Result.bind (fun mode ->
            match mode with
            | "interactive" ->
                create
                    service.AbsoluteUri
                    issuer.AbsoluteUri
                    clientId
                    RemoteClientMode.Interactive
                    None
            | "automation" ->
                required CliSetting.SecretFile
                |> Result.bind (fun path ->
                    if not (IO.Path.IsPathFullyQualified path) then
                        invalid CliSetting.SecretFile
                    else
                        create
                            service.AbsoluteUri
                            issuer.AbsoluteUri
                            clientId
                            RemoteClientMode.Automation
                            (Some path))
            | _ -> invalid CliSetting.AuthMode)

    let private trust value =
        privateRoot CliSetting.OidcTrustRoot
        |> Result.bind (fun oidc ->
            privateRoot CliSetting.ServiceTrustRoot
            |> Result.map (fun service ->
                { value with
                    OidcTrustRootFile = oidc
                    ServiceTrustRootFile = service
                }))

    let fromEnvironment () =
        root CliSetting.ServiceUrl
        |> Result.bind (fun service ->
            if service.AbsolutePath <> "/" then
                invalid CliSetting.ServiceUrl
            else
                root CliSetting.Issuer
                |> Result.bind (fun issuer ->
                    required CliSetting.ClientId
                    |> Result.bind (fun clientId ->
                        if
                            String.IsNullOrWhiteSpace clientId
                            || clientId <> clientId.Trim()
                            || clientId.Length > CliConfigurationLimits.MaximumClientIdCodeUnits
                        then
                            invalid CliSetting.ClientId
                        else
                            configureMode service issuer clientId |> Result.bind trust)))
