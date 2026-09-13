namespace ClaimCore.Application

/// Compiled product identity, separate from protocol/schema/case revisions.
type BuildIdentity =
    {
        Product: string
        Version: string
        AssemblyVersion: string
        FileVersion: string
    }

module BuildIdentity =
    /// SDK-generated attributes of the application core; never a source-file or Git lookup.
    val current: BuildIdentity
    /// Trusted composition refuses a first-party assembly from a different release.
    val requireCompatibleAssembly: assembly: System.Reflection.Assembly -> unit
