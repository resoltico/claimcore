namespace ClaimCore.Postgres

open System
open System.Reflection
open System.Text.Json

type internal SupportedServer = { Major: int; MinimumMinor: int }

module internal ServerSupport =
    let supported =
        let assembly = Assembly.GetExecutingAssembly()

        let name =
            assembly.GetManifestResourceNames()
            |> Array.find _.EndsWith("postgresql-baseline.json", StringComparison.Ordinal)

        use stream = assembly.GetManifestResourceStream name |> nonNull
        use document = JsonDocument.Parse stream

        document.RootElement.GetProperty("supportedMajors").EnumerateArray()
        |> Seq.map (fun entry ->
            {
                Major = entry.GetProperty("major").GetInt32()
                MinimumMinor = entry.GetProperty("minimumMinor").GetInt32()
            })
        |> Seq.toList

    let supports (version: Version) =
        supported
        |> List.exists (fun baseline ->
            version.Major = baseline.Major && version.Minor >= baseline.MinimumMinor)

    let require version =
        if not (supports version) then
            invalidOp "Unsupported PostgreSQL major or minor version."
