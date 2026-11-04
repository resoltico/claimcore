import { useRecovery } from "../recovery/RecoveryContext";
import { usePresentation } from "../presentation/context";
import { followLink } from "../routing/followLink";
import { navigate } from "../routing/navigation";

/** Journal scope and ownership are enforced by the workspace's RecoveryProvider. */
export const RecoveryBanner = () => {
  const recovery = useRecovery();
  const p = usePresentation();
  if (recovery.operations.length === 0) {
    return null;
  }
  return (
    <aside className="shell-warning recovery-banner" aria-label={p.text("ui.recovery")}>
      <p>
        {recovery.operations.length === 1
          ? p.text("ui.shell.unresolvedOne")
          : p.text("ui.shell.unresolved", { count: p.integer(recovery.operations.length) })}
      </p>
      <a
        href="/recovery"
        onClick={(event) => {
          followLink(event, () => {
            navigate("/recovery");
          });
        }}
      >
        {p.text("ui.shell.checkNow")}
      </a>
    </aside>
  );
};
