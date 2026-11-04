import type { Notice } from "../protocol/notices";
import { NoticeView } from "../presentation/Message";
import { guardedDeparture } from "../routing/navigation";
import { useEffect, useRef } from "react";
import type { RequesterState } from "@contracts/client";
import { ActorIdentity } from "../components/ActorIdentity";
import { CopyValue } from "../components/CopyValue";
import { usePresentation } from "../presentation/context";
import { useSessionContext } from "../session/SessionContext";
import { Button } from "../ui/Button";
import "./access-state.css";
const accessMeaning = {
  NOT_REGISTERED: "requesterState.NOT_REGISTERED.meaning",
  NO_ROLES: "requesterState.NO_ROLES.meaning",
  DISABLED: "requesterState.DISABLED.meaning",
} as const;
export const AccessState = ({
  requester,
  locked,
  onCheck,
  feedback,
}: {
  requester: Exclude<RequesterState, { kind: "ACTIVE" }>;
  locked: boolean;
  onCheck: () => void;
  feedback: { message: Notice | null; loading: boolean };
}) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus({ preventScroll: false });
  }, []);
  const { logout } = useSessionContext();
  const details =
    requester.kind === "NOT_REGISTERED"
      ? `${requester.issuer}\n${requester.subject}`
      : requester.self.actorId;
  return (
    <main className="access-state">
      <span className="product-name">
        <span className="product-mark" aria-hidden="true">
          C
        </span>
        ClaimCore
      </span>
      <section>
        <h1 tabIndex={-1} ref={heading}>
          {p.text(
            requester.kind === "DISABLED" ? "ui.accessState.disabled" : "ui.accessState.title",
          )}
        </h1>
        <p>{p.text(accessMeaning[requester.kind])}</p>
        {requester.kind === "NOT_REGISTERED" ? <p>{p.text("ui.accessState.requested")}</p> : null}
        <dl>
          {requester.kind === "NOT_REGISTERED" ? (
            <>
              <dt>{p.text("ui.account")}</dt>
              <dd>
                <bdi>{requester.subject}</bdi>
              </dd>
              <dt>{p.text("ui.accessState.provider")}</dt>
              <dd>
                <bdi dir="ltr">{requester.issuer}</bdi>
              </dd>
            </>
          ) : (
            <>
              <dt>{p.text("ui.account")}</dt>
              <dd>
                <ActorIdentity actor={requester.self} />
              </dd>
            </>
          )}
        </dl>
        <CopyValue label={p.text("ui.accessState.copy")} value={details} />
        {feedback.message === null ? null : (
          <p role="alert">
            <NoticeView value={feedback.message} />
          </p>
        )}
        {feedback.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
        <Button isDisabled={locked || feedback.loading} onPress={onCheck} variant="secondary">
          {p.text("ui.accessState.check")}
        </Button>
        <Button
          isDisabled={locked}
          onPress={() => {
            guardedDeparture(() => {
              void logout();
            });
          }}
          variant="secondary"
        >
          {p.text("ui.signOut")}
        </Button>
      </section>
    </main>
  );
};
