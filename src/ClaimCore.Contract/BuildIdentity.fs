namespace ClaimCore.Contract

open System
open System.Reflection

/// Compiled product identity, separate from protocol/schema/case revisions.
type BuildIdentity =
    {
        Product: string
        Version: string
        AssemblyVersion: string
        FileVersion: string
    }

module BuildIdentity =
    let private requiredText value =
        match Option.ofObj value with
        | Some text when not (String.IsNullOrWhiteSpace text) -> Ok text
        | _ -> Error "Required compiled release metadata is missing. Rebuild the complete project."

    let private attribute<'T when 'T :> Attribute> (assembly: Assembly) =
        match assembly.GetCustomAttributes(typeof<'T>, false) with
        | [| value |] -> Ok(value :?> 'T)
        | _ -> Error "Required compiled release attribute is missing or duplicated."

    let private textAttribute<'T when 'T :> Attribute> get assembly =
        attribute<'T> assembly |> Result.bind (get >> requiredText)

    let private read (assembly: Assembly) =
        match assembly.GetName().Version |> Option.ofObj with
        | None -> Error "Compiled assembly version is missing."
        | Some numeric ->
            textAttribute<AssemblyProductAttribute> _.Product assembly
            |> Result.bind (fun product ->
                textAttribute<AssemblyInformationalVersionAttribute>
                    _.InformationalVersion
                    assembly
                |> Result.bind (fun version ->
                    textAttribute<AssemblyFileVersionAttribute> _.Version assembly
                    |> Result.map (fun file ->
                        {
                            Product = product
                            Version = version
                            AssemblyVersion = numeric.ToString()
                            FileVersion = file
                        })))

    let current = read typeof<BuildIdentity>.Assembly |> Result.defaultWith invalidOp

    let requireCompatibleAssembly assembly =
        read assembly
        |> Result.bind (fun identity ->
            if identity = current then
                Ok()
            else
                Error
                    "Mixed first-party release identities are unsupported. Deploy one complete build.")
        |> Result.defaultWith invalidOp
