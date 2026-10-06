namespace ClaimCore.Hosting

open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Postgres


module internal RuntimeActorFactory =
    let create (resources: RuntimeResources) context =
        let artifactAuthority =
            { new IRecoveryArtifactAuthority with
                member _.Sign(retained, ct) =
                    RecoveryArtifactAuthority.sign
                        resources.DataSource
                        resources.Witness
                        resources.Clock
                        context
                        resources.ArtifactKeyRingPath
                        retained
                        ct

                member _.Verify(source, ct) =
                    RecoveryArtifactAuthority.verify
                        resources.DataSource
                        resources.Witness
                        resources.Clock
                        context
                        resources.ArtifactKeyRingPath
                        source
                        ct
            }

        let claims =
            new PostgresStore(
                resources.DataSource,
                resources.Witness,
                context,
                resources.CursorProtection
            )

        let recovery =
            new PostgresRecoveryStore(
                resources.DataSource,
                PreparationLimits.defaults,
                resources.Witness,
                context
            )

        CoreApi.createActor
            (claims :> IClaimStore)
            (recovery :> IRecoveryStore)
            resources.Clock
            context
            artifactAuthority
