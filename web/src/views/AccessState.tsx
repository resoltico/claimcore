import type { Notice } from "../protocol/notices";
import { NoticeView } from "../presentation/Message";
import { guardedDeparture } from "../routing/navigation";
import { useEffect, useRef } from "react";
import type { HumanReadDocument_context } from "@contracts/client";
import { ScreenView } from "../ui/ScreenView";
import { usePresentation } from "../presentation/context";
import { useSessionContext } from "../session/SessionContext";
import { Button } from "../ui/Button";
import "./access-state.css";
export const AccessState = ({
  requester,
  locked,
  onCheck,
  feedback,
}: {
  requester: HumanReadDocument_context;
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
  if (requester.context.state === "ACTIVE") {
    return null;
  }
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
            requester.context.state === "DISABLED"
              ? "ui.accessState.disabled"
              : "ui.accessState.title",
          )}
        </h1>
        {requester.context.state === "NOT_REGISTERED" ? (
          <p>{p.text("ui.accessState.requested")}</p>
        ) : null}
        <ScreenView
          view={requester.screen}
          timeZone={
            requester.basis.kind === "SNAPSHOT" ? requester.basis.observation.timeZone : "UTC"
          }
        />
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
