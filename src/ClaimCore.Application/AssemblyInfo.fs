namespace ClaimCore.Application

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("ClaimCore.Postgres")>]
[<assembly: InternalsVisibleTo("ClaimCore.Web")>]
[<assembly: InternalsVisibleTo("ClaimCore.Tests")>]
[<assembly: InternalsVisibleTo("ClaimCore.WebTests")>]
[<assembly: InternalsVisibleTo("ClaimCore.IntegrationTests")>]
do ()
