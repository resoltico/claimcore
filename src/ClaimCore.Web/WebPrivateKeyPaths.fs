namespace ClaimCore.Web

open System.IO
open ClaimCore.Contracts

module internal WebPrivateKeyPaths =
    let requireAbsolute (required: WebSetting -> string) setting refusal =
        let path = required setting

        if not (Path.IsPathFullyQualified(path)) then
            WebStartupDiagnostics.refuse refusal

        path
