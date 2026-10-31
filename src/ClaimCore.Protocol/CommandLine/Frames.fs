namespace ClaimCore.Protocol


open System
open ClaimCore.Json

type VersionFrame = BuildIdentity

module VersionFrame =
    open Codec

    let fields =
        record (fun product version assembly file ->
            {
                Product = product
                Version = version
                AssemblyVersion = assembly
                FileVersion = file
            })
        |> field "product" _.Product text
        |> field "version" _.Version text
        |> field "assemblyVersion" _.AssemblyVersion text
        |> field "fileVersion" _.FileVersion text

    let codec = fields |> build |> named "VersionFrame"

type HelpFrame =
    {
        Acts: string list
        ExitCodes: (string * int) list
    }

module HelpFrame =
    open Codec

    let private exitEntry =
        record (fun code number -> code, number)
        |> field "code" fst text
        |> field "number" snd (integer 0L 255L |> map int int64)
        |> build

    let fields =
        record (fun acts codes -> { Acts = acts; ExitCodes = codes })
        |> field "acts" _.Acts (list Int32.MaxValue text)
        |> field "exitCodes" _.ExitCodes (list Int32.MaxValue exitEntry)

    let codec = fields |> build |> named "HelpFrame"

type UsageFrame =
    {
        Issue: UsageError
        Usage: string list
    }

module UsageFrame =
    open Codec

    let fields =
        record (fun issue usage -> { Issue = issue; Usage = usage })
        |> field "issue" _.Issue UsageError.codec
        |> field "usage" _.Usage (list Int32.MaxValue text)

    let codec = fields |> build |> named "UsageFrame"
