namespace ClaimCore.Cli

open System.Reflection

module BuildIdentity =
    let current =
        let assembly = Assembly.GetExecutingAssembly()

        {|
            Product = (assembly.GetCustomAttribute<AssemblyProductAttribute>() |> nonNull).Product
            Version =
                (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() |> nonNull)
                    .InformationalVersion
            AssemblyVersion = assembly.GetName().Version |> nonNull |> string
            FileVersion =
                (assembly.GetCustomAttribute<AssemblyFileVersionAttribute>() |> nonNull).Version
        |}
