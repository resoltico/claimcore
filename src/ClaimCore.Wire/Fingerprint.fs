namespace ClaimCore.Wire

open System
open System.Security.Cryptography

module Fingerprint =
    let value = OpenApi.emit () |> SHA256.HashData |> Convert.ToHexStringLower
