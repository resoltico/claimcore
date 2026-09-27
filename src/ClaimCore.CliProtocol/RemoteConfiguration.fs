namespace ClaimCore.Cli

open System
open ClaimCore.Contracts

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
            && clientId.Length <= 256
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
            | _ -> Error ProtocolProblem.ServiceConfigurationInvalid
        | _ -> Error ProtocolProblem.ServiceConfigurationInvalid

    let fromEnvironment () =
        let read = Environment.GetEnvironmentVariable

        match
            read "CLAIMCORE_SERVICE_URL",
            read "CLAIMCORE_OIDC_ISSUER",
            read "CLAIMCORE_OIDC_CLIENT_ID",
            read "CLAIMCORE_CLI_AUTH_MODE"
        with
        | null, _, _, _
        | _, null, _, _
        | _, _, null, _
        | _, _, _, null -> Error ProtocolProblem.ServiceConfigurationMissing
        | service, issuer, clientId, mode ->
            let configured =
                match mode with
                | "interactive" -> create service issuer clientId RemoteClientMode.Interactive None
                | "automation" ->
                    let path = read "CLAIMCORE_OIDC_CLIENT_SECRET_FILE" |> Option.ofObj
                    create service issuer clientId RemoteClientMode.Automation path
                | _ -> Error ProtocolProblem.ServiceConfigurationInvalid

            let privateRoot name =
                match read name |> Option.ofObj with
                | None -> Ok None
                | Some path when IO.Path.IsPathFullyQualified(path) -> Ok(Some path)
                | Some _ -> Error ProtocolProblem.ServiceConfigurationInvalid

            configured
            |> Result.bind (fun value ->
                privateRoot "CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE"
                |> Result.bind (fun oidcRoot ->
                    privateRoot "CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE"
                    |> Result.map (fun serviceRoot ->
                        { value with
                            OidcTrustRootFile = oidcRoot
                            ServiceTrustRootFile = serviceRoot
                        })))
