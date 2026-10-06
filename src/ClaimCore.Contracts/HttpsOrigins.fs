namespace ClaimCore.Contracts

open System

/// Local HTTPS names include the reserved .localhost namespace used by container aliases.
module HttpsOrigins =
    let isLocal (endpoint: Uri) =
        endpoint.Scheme = Uri.UriSchemeHttps
        && (endpoint.IsLoopback
            || endpoint.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
