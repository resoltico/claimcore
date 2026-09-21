namespace ClaimCore.Application

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("ClaimCore.Postgres")>]
[<assembly: InternalsVisibleTo("ClaimCore.Hosting")>]
[<assembly: InternalsVisibleTo("ClaimCore.Tests")>]
[<assembly: InternalsVisibleTo("ClaimCore.IntegrationTests")>]
[<assembly: InternalsVisibleTo("ClaimCore.FuzzQualificationTests")>]
do ()
