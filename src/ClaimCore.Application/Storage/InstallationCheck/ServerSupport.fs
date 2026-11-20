namespace ClaimCore.Application.Storage

open System
open System.Globalization
open System.Reflection
open System.Text.Json
open System.Text.RegularExpressions
open ClaimCore.Application.Contracts

type internal SupportedServer =
    {
        Major: int
        MinimumMinor: int
        ContainerImage: string
        InitdbArguments: string
    }

type internal ObservedDatabase =
    {
        Encoding: string
        Provider: string
        Locale: string
        Checksums: string
    }

module internal ServerSupport =
    let parse (stream: IO.Stream) =
        use document = JsonDocument.Parse stream
        let root = document.RootElement
        let entries = root.GetProperty("supportedMajors")

        if entries.GetArrayLength() <> 1 then
            invalidOp "A PostgreSQL baseline requires exactly one pinned major."

        let initdbArguments = root.GetProperty("initdbArguments").GetString() |> nonNull

        if String.IsNullOrWhiteSpace initdbArguments then
            invalidOp "The PostgreSQL baseline requires nonempty initdb arguments."

        let entry = entries[0]
        let major = entry.GetProperty("major").GetInt32()
        let minor = entry.GetProperty("minimumMinor").GetInt32()
        let image = entry.GetProperty("containerImage").GetString() |> nonNull

        let tag =
            Regex.Match(image, @"\Apostgres:([1-9][0-9]*)\.([0-9]+)@sha256:[0-9a-f]{64}\z")

        if
            major < 1
            || minor < 0
            || not tag.Success
            || tag.Groups[1].Value <> major.ToString(CultureInfo.InvariantCulture)
            || Int32.Parse(tag.Groups[2].Value, CultureInfo.InvariantCulture) < minor
        then
            invalidOp "The PostgreSQL baseline requires a matching pinned major and minimum minor."

        {
            Major = major
            MinimumMinor = minor
            ContainerImage = image
            InitdbArguments = initdbArguments
        }

    let supported =
        let assembly = Assembly.GetExecutingAssembly()

        let name =
            assembly.GetManifestResourceNames()
            |> Array.find _.EndsWith("postgresql-baseline.json", StringComparison.Ordinal)

        use stream = assembly.GetManifestResourceStream name |> nonNull
        parse stream

    let versionFault baseline (version: Version) =
        if version.Major = baseline.Major && version.Minor >= baseline.MinimumMinor then
            None
        else
            Some OperatorFault.UnsupportedServer

    let profileFault profile =
        if
            profile.Encoding = "UTF8"
            && profile.Provider = "b"
            && profile.Locale = "C.UTF-8"
            && profile.Checksums = "on"
        then
            None
        else
            Some OperatorFault.UnsupportedDatabase
