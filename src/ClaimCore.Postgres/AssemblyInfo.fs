namespace ClaimCore.Postgres

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("ClaimCore.Hosting")>]
[<assembly: InternalsVisibleTo("ClaimCore.Database")>]
[<assembly: InternalsVisibleTo("ClaimCore.IntegrationTests")>]
do ()
