namespace ClaimCore.Application

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
        | Some text when not (String.IsNullOrWhiteSpace(text)) -> text
        | _ ->
            invalidOp "Required compiled release metadata is missing. Rebuild the complete project."

    let private attribute<'T when 'T :> Attribute> (assembly: Assembly) =
        match assembly.GetCustomAttributes(typeof<'T>, false) with
        | [| value |] -> value :?> 'T
        | _ -> invalidOp "Required compiled release attribute is missing or duplicated."

    let private read (assembly: Assembly) =
        let numeric =
            assembly.GetName().Version
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Compiled assembly version is missing.")

        {
            Product = (attribute<AssemblyProductAttribute> assembly).Product |> requiredText
            Version =
                (attribute<AssemblyInformationalVersionAttribute> assembly).InformationalVersion
                |> requiredText
            AssemblyVersion = numeric.ToString()
            FileVersion = (attribute<AssemblyFileVersionAttribute> assembly).Version |> requiredText
        }

    let current = read typeof<BuildIdentity>.Assembly

    let requireCompatibleAssembly assembly =
        if read assembly <> current then
            invalidOp
                "Mixed first-party release identities are unsupported. Deploy one complete build."
