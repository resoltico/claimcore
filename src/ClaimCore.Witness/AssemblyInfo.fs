namespace ClaimCore.Witness

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("ClaimCore.Postgres")>]
[<assembly: InternalsVisibleTo("ClaimCore.Database")>]
[<assembly: InternalsVisibleTo("ClaimCore.IntegrationTests")>]
do ()
