import type { ActorRef } from "@contracts/client";
import { actorName } from "../presentation/actorName";
import { CharacterWarning } from "./BusinessValue";
import { usePresentation } from "../presentation/context";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { CopyValue } from "./CopyValue";
import { followLink } from "../routing/followLink";
import { navigate } from "../routing/navigation";
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
/** Only DTOs that still supply a bare UUID use this technical identity, never an inferred name. */
export const TechnicalActorIdentity = ({ actorId }: { actorId: string }) => {
  const p = usePresentation();
  const context = useRequesterContext();
  const label = actorId.slice(0, 8);
  const href = `/people/${encodeURIComponent(actorId)}`;
  return (
    <span className="actor-identity">
      {context?.capabilities.includes("READ_AUTHORITY") === true ? (
        <a
          href={href}
          title={actorId}
          onClick={(event) => {
            followLink(event, () => {
              navigate(href);
            });
          }}
        >
          {label}
        </a>
      ) : (
        <bdi dir="ltr" title={actorId}>
          {label}
        </bdi>
      )}
      <CopyValue icon label={p.text("ui.actor")} value={actorId} />
    </span>
  );
};
export const OperationIdentity = ({ operationId }: { operationId: string }) => {
  const p = usePresentation();
  return (
    <span className="operation-identity">
      <bdi dir="ltr" title={operationId}>
        {operationId.slice(0, 8)}
      </bdi>
      <CopyValue icon label={p.fieldLabel("operationId")} value={operationId} />
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
