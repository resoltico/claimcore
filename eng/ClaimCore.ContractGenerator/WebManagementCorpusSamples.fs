namespace ClaimCore.ContractGeneration

open System
open ClaimCore.Application
open ClaimCore.Contracts

module internal WebManagementCorpusSamples =
    let private eventId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")
    let private actorId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")

    let all =
        [
            for endpoint in
                [
                    "authority.register"
                    "authority.setGrant"
                    "authority.setEnabled"
                    "authority.observe"
                ] do
                yield
                    WebCorpusSamples.sample
                        (endpoint + "-applied")
                        endpoint
                        (WebWireCodec.management
                            endpoint
                            (ActorManagementOutcome.Applied(eventId, 2L, actorId)))

                yield
                    WebCorpusSamples.sample
                        (endpoint + "-unavailable")
                        endpoint
                        (WebWireCodec.management endpoint ActorManagementOutcome.ResourceUnavailable)

                yield
                    WebCorpusSamples.sample
                        (endpoint + "-unconfirmed")
                        endpoint
                        (WebWireCodec.management
                            endpoint
                            (ActorManagementOutcome.Unconfirmed eventId))
        ]
