namespace ClaimCore.Web

open ClaimCore.Application
open ClaimCore.Hosting

/// Web can render only the public typed core facade. Recovery remains reachable through
/// `IClaimsCore.Recovery`; neither the runtime nor the host exposes a technical store or preparation.
type IWebRuntime =
    abstract Core: IClaimsCore

module WebRuntime =
    let fromRuntime (runtime: Runtime) : IWebRuntime =
        { new IWebRuntime with
            member _.Core = runtime.Core
        }
