import { useCallback, useEffect, useRef } from "react";
import type { Notice } from "../protocol/notices";
import { type ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { ActorSummary } from "./AuthorityListing";

const grantPage = (response: ServiceResponse<"authority.grants">) =>
  response.outcome.kind === "SUCCEEDED" ? response.outcome.data : null;
type GrantPage = NonNullable<ReturnType<typeof grantPage>>;
type Props = {
  actorId: string;

  onBack: () => void;
  onDisclosureRefused: (notice?: Notice) => void;
  onActor?: ((actor: GrantPage["actor"]) => void) | undefined;
};
const GrantItems = ({ items }: { items: GrantPage["items"] }) => {
  const p = usePresentation();
  return (
    <ul>
      {items.map((grant) => (
        <li
          key={`${grant.role}:${grant.scope.kind}:${grant.scope.kind === "CASE" ? grant.scope.caseReference : ""}`}
        >
          {p.term("role", grant.role)} · {p.term("scope", grant.scope.kind)}{" "}
          {grant.scope.kind === "CASE" ? <bdi>{grant.scope.caseReference}</bdi> : null}
        </li>
      ))}
    </ul>
  );
};
const useActorDisclosure = (actor: GrantPage["actor"] | null, onActor: Props["onActor"]) => {
  useEffect(() => {
    if (actor !== null) {
      onActor?.(actor);
    }
  }, [actor, onActor]);
};
export const AuthorityGrants = ({ actorId, onBack, onDisclosureRefused, onActor }: Props) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, []);
  const input = useCallback(
    (cursor: string | null) => ({ actorId, ...(cursor === null ? {} : { cursor }), limit: 50 }),
    [actorId],
  );
  const page = useEndpointPage<"authority.grants", GrantPage["items"][number], GrantPage>(
    "authority.grants",
    input,
    grantPage,
    onDisclosureRefused,
  );

  useActorDisclosure(page.page?.actor ?? null, onActor);

  return (
    <section aria-labelledby="actor-grants-title">
      <h1 id="actor-grants-title" tabIndex={-1} ref={heading}>
        {p.text("ui.actorGrants")}
      </h1>
      <button type="button" onClick={onBack}>
        {p.text("ui.backToActors")}
      </button>
      {page.page === null ? null : <ActorSummary actor={page.page.actor} />}
      <GrantItems items={page.items} />
      {page.message === null ? null : (
        <p role="alert">
          <NoticeView value={page.message} />
        </p>
      )}
      {page.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {page.page === null ? null : (
        <p role="status">{p.text(page.cursor === null ? "ui.grantsEnd" : "ui.grantsPartial")}</p>
      )}
      <button type="button" disabled={page.loading} onClick={() => void page.load(null)}>
        {p.text("ui.reloadGrants")}
      </button>
      {page.cursor === null ? null : (
        <button type="button" disabled={page.loading} onClick={() => void page.load(page.cursor)}>
          {p.text("ui.moreGrants")}
        </button>
      )}
    </section>
  );
};
