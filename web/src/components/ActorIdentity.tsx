import { ActorAvatar } from "./ActorAvatar";
import type { ActorRef } from "@contracts/client";
import { actorName } from "../presentation/actorName";
import { CharacterWarning } from "./CharacterWarning";
import { usePresentation } from "../presentation/context";
export const ActorIdentity = ({ actor, avatar = false }: { actor: ActorRef; avatar?: boolean }) => {
  const p = usePresentation();
  return (
    <span className="actor-identity">
      {avatar ? <ActorAvatar actor={actor} /> : null}
      <bdi>{actorName(p, actor)}</bdi>
      <CharacterWarning
        value={actor.displayName.kind === "NAMED" ? actor.displayName.value : null}
      />
    </span>
  );
};
