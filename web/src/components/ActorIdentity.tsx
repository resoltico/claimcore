import type { ActorRef } from "@contracts/client";
import { actorName } from "../presentation/actorName";
import { CharacterWarning } from "./BusinessValue";
import { usePresentation } from "../presentation/context";
import { useRequesterContext } from "../hooks/useRequesterContext";
export const ActorIdentity = ({ actor }: { actor: ActorRef }) => {
  const p = usePresentation();
  return (
    <span className="actor-identity">
      <bdi>{actorName(p, actor)}</bdi>
      <CharacterWarning
        value={actor.displayName.kind === "NAMED" ? actor.displayName.value : null}
      />
    </span>
  );
};
export const RecordedTime = ({ value }: { value: string }) => {
  const p = usePresentation();
  const context = useRequesterContext();
  return (
    <time dateTime={value} title={value}>
      {context === null ? value : p.recordedTime(value, context.timeZone)}
    </time>
  );
};
