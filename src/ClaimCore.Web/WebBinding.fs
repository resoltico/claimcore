namespace ClaimCore.Web

open System
open System.Net
open System.Net.Sockets
open ClaimCore.Contracts

/// Public authority and socket binding are distinct, including through Docker port publication.
[<NoEquality; NoComparison>]
type WebBinding =
    {
        Origin: Uri
        ListenAddress: IPAddress
        ListenPort: int
    }

module WebBindings =
    let parseOrigin (value: string) =
        try
            let parsed = Uri(value, UriKind.Absolute)

            if
                parsed.Scheme <> Uri.UriSchemeHttps
                || parsed.PathAndQuery <> "/"
                || not (String.IsNullOrEmpty(parsed.Fragment))
                || not (String.IsNullOrEmpty(parsed.UserInfo))
                || parsed.HostNameType = UriHostNameType.Unknown
                || parsed.Host.EndsWith(".", StringComparison.Ordinal)
            then
                WebStartupDiagnostics.refuse WebStartupProblem.OriginInvalid

            parsed
        with :? UriFormatException ->
            WebStartupDiagnostics.refuse WebStartupProblem.OriginInvalid

    let create (origin: Uri) (address: string) port =
        let parsed =
            match IPAddress.TryParse(address) with
            | true, value -> Option.ofObj value
            | _ -> None

        let ip =
            match parsed with
            | Some value when
                (value.AddressFamily = AddressFamily.InterNetwork
                 || value.AddressFamily = AddressFamily.InterNetworkV6)
                && value <> IPAddress.Broadcast
                ->
                value
            | _ ->
                WebStartupDiagnostics.refuse (
                    WebStartupProblem.InvalidSetting WebSetting.ListenAddress
                )

        if port < 1 || port > 65535 then
            WebStartupDiagnostics.refuse (WebStartupProblem.InvalidSetting WebSetting.ListenPort)

        {
            Origin = origin
            ListenAddress = ip
            ListenPort = port
        }

    let acceptsPeer (endpoint: WebBinding) (peer: IPAddress | null) =
        match Option.ofObj peer with
        | None -> false
        | Some value ->
            not (IPAddress.IsLoopback endpoint.ListenAddress) || IPAddress.IsLoopback value
