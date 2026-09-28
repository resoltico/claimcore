namespace ClaimCore.Cli

open ClaimCore.HostSecurity

module PrivateFiles =
    let readSource maximum path =
        PrivateFileService.readUtf8Bytes maximum path

    let writeNew destination bytes =
        PrivateFileService.writeNew 131072 destination bytes
