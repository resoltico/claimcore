namespace ClaimCore.Server

open System
open System.IO
open System.Reflection

/// Source catalogs for native rendering.
module internal NativeCatalog =
    let internal core language =
        use stream =
            Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream("ClaimCore.Localization." + language + ".core.json")
            |> nonNull

        use bytes = new MemoryStream()
        stream.CopyTo bytes
        bytes.ToArray()
