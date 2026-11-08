import { PageTables } from "../ui/PageTables";
import { useCallback, useEffect, type ReactNode } from "react";
import type { Notice } from "../protocol/notices";
import { type ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { CharacterWarning } from "../components/BusinessValue";
import { IconButton } from "../ui/IconButton";
import { Dialog } from "../ui/Dialog";
import { Button } from "../ui/Button";
import { actorName } from "../presentation/actorName";
import { useSettingsDetour } from "../hooks/SettingsDetourContext";
import { followLink } from "../routing/followLink";
import { navigate } from "../routing/navigation";
import { ActorSummary } from "../components/ActorSummary";

const grantPage = (response: ServiceResponse<"authority.grants">) =>
  response.outcome.kind === "SUCCEEDED" ? response.outcome.data : null;
type GrantPage = NonNullable<ReturnType<typeof grantPage>>;
type Props = {
  actorId: string;

  onBack: () => void;
  onDisclosureRefused: (notice?: Notice) => void;
  onActor?: ((actor: GrantPage["actor"]) => void) | undefined;
  open?: boolean;
  actions?: ReactNode;
  onRevoke?: ((grant: GrantPage["items"][number]) => void) | undefined;
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
const PersonContents = ({
  page,
  actions,
  onRevoke,
}: {
  onRevoke: Props["onRevoke"];
  page: ReturnType<typeof useActorGrants>;
  actions: ReactNode;
}) => {
  const p = usePresentation();
  return (
    <>
      {page.page === null ? null : <ActorSummary actor={page.page.actor} identityInHeader />}
      {actions}
      <PageTables
        views={page.views}
        renderActions={(index) => {
          const grant = page.items[index];
          return grant === undefined || onRevoke === undefined ? null : (
            <button
              type="button"
              onClick={() => {
                onRevoke(grant);
              }}
            >
              {p.authorityLabel("REVOKE")}
            </button>
          );
        }}
      />
      {page.message === null ? null : (
        <p role="alert">
          <NoticeView value={page.message} />
        </p>
      )}
      {page.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {page.page === null ? null : (
        <a
          className="actor-detail-link"
          href={`/authority-history?actorId=${encodeURIComponent(page.page.actor.actor.actorId)}`}
          onClick={(event) => {
            followLink(event, () => {
              navigate(
                `/authority-history?actorId=${encodeURIComponent(page.page!.actor.actor.actorId)}`,
              );
            });
          }}
        >
          {p.text("ui.shell.authorityHistory")}
        </a>
      )}
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
    </>
  );
};
export const AuthorityGrants = ({
  actorId,
  onBack,
  onDisclosureRefused,
  onActor,
  open = true,
  actions,
  onRevoke,
}: Props) => {
  const p = usePresentation();
  const settings = useSettingsDetour();
  const page = useActorGrants(actorId, onDisclosureRefused);

  useActorDisclosure(page.page?.actor ?? null, onActor);

  return (
    <Dialog
      title={page.page === null ? p.text("ui.people.person") : actorName(p, page.page.actor.actor)}
      {...(page.page === null
        ? {}
        : { description: p.term("principalKind", page.page.actor.principal.kind) })}
      identity={
        page.page?.actor.actor.displayName.kind === "NAMED" ? (
          <CharacterWarning value={page.page.actor.actor.displayName.value} />
        ) : null
      }
      closeLabel={p.text("ui.close")}
      placement="end"
      headerEnd={
        <div className="actions">
          {settings === null ? null : (
            <IconButton icon="settings" label={p.text("ui.settings")} onPress={settings.visit} />
          )}
          <IconButton icon="close" label={p.text("ui.close")} onPress={onBack} />
        </div>
      }
      isOpen={open && settings?.paused !== true}
      isDismissable
      onOpenChange={(next) => {
        if (!next) {
          onBack();
        }
      }}
      footer={
        <Button variant="secondary" onPress={onBack}>
          {p.text("ui.close")}
        </Button>
      }
    >
      <PersonContents page={page} actions={actions} onRevoke={onRevoke} />
    </Dialog>
  );
};
