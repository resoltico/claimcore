namespace ClaimCore.Postgres

open System
open System.Reflection
open System.Text.Json

module internal ServerSupport =
    let major =
        let assembly = Assembly.GetExecutingAssembly()

        let name =
            assembly.GetManifestResourceNames()
            |> Array.find _.EndsWith("postgresql-baseline.json", StringComparison.Ordinal)

        use stream = assembly.GetManifestResourceStream name |> nonNull
        use document = JsonDocument.Parse stream
        document.RootElement.GetProperty("major").GetInt32()

    let requireMajor value =
        if value <> major then
            invalidOp "Unsupported PostgreSQL major version."
