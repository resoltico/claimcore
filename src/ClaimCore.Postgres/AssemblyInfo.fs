namespace ClaimCore.Postgres

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("claimcore-server")>]
[<assembly: InternalsVisibleTo("ClaimCore.IntegrationTests")>]
[<assembly: InternalsVisibleTo("ClaimCore.WebTests")>]
[<assembly: InternalsVisibleTo("ClaimCore.TestSupport")>]
do ()
