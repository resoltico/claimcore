import { IdentifierValue } from "../components/IdentifierValue";
import { useCallback, useEffect, useRef } from "react";
import type { Notice } from "../protocol/notices";
import { type ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { ActorSummary, type KnownActorGrantCount } from "./AuthorityListing";

const grantPage = (response: ServiceResponse<"authority.grants">) =>
  response.outcome.kind === "SUCCEEDED" ? response.outcome.data : null;
type GrantPage = NonNullable<ReturnType<typeof grantPage>>;
type Props = {
  actorId: string;

  onBack: () => void;
  onDisclosureRefused: (notice?: Notice) => void;
  onActor?: ((actor: GrantPage["actor"]) => void) | undefined;
  onGrantCount?: ((count: KnownActorGrantCount | null) => void) | undefined;
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
          {grant.scope.kind === "CASE" ? (
            <IdentifierValue value={grant.scope.caseReference} />
          ) : null}
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
const useActorGrants = (actorId: string, onDisclosureRefused: Props["onDisclosureRefused"]) => {
  const input = useCallback(
    (cursor: string | null) => ({ actorId, ...(cursor === null ? {} : { cursor }), limit: 50 }),
    [actorId],
  );
  return useEndpointPage<"authority.grants", GrantPage["items"][number], GrantPage>(
    "authority.grants",
    input,
    grantPage,
    onDisclosureRefused,
  );
};
const useGrantCount = (
  page: ReturnType<typeof useActorGrants>,
  onGrantCount: Props["onGrantCount"],
) => {
  const actorId = page.page?.actor.actorId ?? null;
  const count =
    page.page !== null && !page.loading && page.message === null && page.cursor === null
      ? page.items.length
      : null;
  useEffect(() => {
    onGrantCount?.(actorId === null || count === null ? null : { actorId, count });
    return () => {
      onGrantCount?.(null);
    };
  }, [actorId, count, onGrantCount]);
};
export const AuthorityGrants = ({
  actorId,
  onBack,
  onDisclosureRefused,
  onActor,
  onGrantCount,
}: Props) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, []);
  const page = useActorGrants(actorId, onDisclosureRefused);
  useGrantCount(page, onGrantCount);

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
