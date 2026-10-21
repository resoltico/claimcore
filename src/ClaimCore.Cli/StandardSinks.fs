namespace ClaimCore.Cli

open System
open System.IO
open Microsoft.Win32.SafeHandles

/// Select once before writing. Console preserves shared offsets/append; borrowed streams expose EPIPE.
module StandardSinks =
    let supportsRemote = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()

    let private openSink (descriptor: int) console =
        if not supportsRemote then
            console ()
        else
            let handle = new SafeFileHandle(IntPtr descriptor, ownsHandle = false)

            try
                let candidate =
                    new FileStream(handle, FileAccess.Write, bufferSize = 1, isAsync = false)

                if candidate.CanSeek then
                    candidate.Dispose()
                    console ()
                else
                    candidate :> Stream
            with _ ->
                handle.Dispose()
                reraise ()

    let output () = openSink 1 Console.OpenStandardOutput
    let errors () = openSink 2 Console.OpenStandardError
