import type { ReactNode } from "react";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import "./empty-state.css";

export const EmptyState = ({ messageId, action }: { messageId: string; action?: ReactNode }) => {
  const p = usePresentation();
  return (
    <div className="empty-state">
      <p>{renderCatalogKey(p, messageId)}</p>
      {action}
    </div>
  );
};
