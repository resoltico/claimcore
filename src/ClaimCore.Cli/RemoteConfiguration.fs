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

    let private invalid setting =
        Error(ClientFailure.ConfigurationIssue(setting, ConfigurationIssue.Invalid))

    let private required read setting =
        match read setting with
        | None -> Error(ClientFailure.ConfigurationIssue(setting, ConfigurationIssue.Missing))
        | Some value -> Ok value

    let private root read setting =
        required read setting
        |> Result.bind (fun value ->
            match absoluteHttpsRoot value with
            | Some uri -> Ok uri
            | None -> invalid setting)

    let private privateRoot (read: CliSetting -> string option) setting =
        match read setting with
        | None -> Ok None
        | Some path when IO.Path.IsPathFullyQualified path -> Ok(Some path)
        | _ -> invalid setting

    let private configureMode read (service: Uri) (issuer: Uri) clientId =
        required read CliSetting.AuthMode
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
                required read CliSetting.SecretFile
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

    let private trust read value =
        privateRoot read CliSetting.OidcTrustRoot
        |> Result.bind (fun oidc ->
            privateRoot read CliSetting.ServiceTrustRoot
            |> Result.map (fun service ->
                { value with
                    OidcTrustRootFile = oidc
                    ServiceTrustRootFile = service
                }))

    let private connection read =
        root read CliSetting.ServiceUrl
        |> Result.bind (fun service ->
            if service.AbsolutePath <> "/" then
                invalid CliSetting.ServiceUrl
            else
                root read CliSetting.Issuer
                |> Result.bind (fun issuer ->
                    required read CliSetting.ClientId
                    |> Result.bind (fun clientId ->
                        if
                            String.IsNullOrWhiteSpace clientId
                            || clientId <> clientId.Trim()
                            || clientId.Length > CliConfigurationLimits.MaximumClientIdCodeUnits
                        then
                            invalid CliSetting.ClientId
                        else
                            Ok(service, issuer, clientId))))

    let internal fromSettings read =
        connection read
        |> Result.bind (fun (service, issuer, clientId) ->
            configureMode read service issuer clientId |> Result.bind (trust read))

    let internal validatePublic values =
        let read setting = Map.tryFind setting values

        connection read
        |> Result.bind (fun _ ->
            required read CliSetting.AuthMode
            |> Result.bind (fun mode ->
                if mode = "interactive" || mode = "automation" then
                    Ok()
                else
                    invalid CliSetting.AuthMode))
        |> Result.bind (fun () -> privateRoot read CliSetting.ServiceTrustRoot)
        |> Result.bind (fun _ -> privateRoot read CliSetting.OidcTrustRoot)
        |> Result.map ignore

    let internal sources readEnvironment =
        CliProfile.location readEnvironment
        |> Result.bind (fun path ->
            CliProfile.read path
            |> Result.map (fun profile ->
                let values =
                    InputDiagnostics.settings
                    |> List.map (fun (setting, name) ->
                        let value, source =
                            match readEnvironment name with
                            | Some value -> Some value, "environment"
                            | None ->
                                match Map.tryFind setting profile with
                                | Some value -> Some value, "profile"
                                | None -> None, "missing"

                        setting, (value, source))
                    |> Map.ofList

                path, values))

    let internal environment name =
        Environment.GetEnvironmentVariable name |> Option.ofObj

    let load () =
        sources environment
        |> Result.bind (fun (_, values) -> fromSettings (fun setting -> fst values[setting]))
