import type { RegionContent } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { PresentedValue } from "../../components/PresentedValue";
import { Icon } from "../icons";
import "./notice.css";
export const Notice = ({
  content,
  timeZone,
}: {
  content: Extract<RegionContent, { kind: "NOTICE" }>;
  timeZone: string;
}) => {
  const p = usePresentation();
  return (
    <div className="region-notice" data-tone={content.tone} role="status">
      <Icon name="check" />
      <div>
        <p>{renderCatalogKey(p, content.messageId)}</p>
        {content.reason === undefined ? null : (
          <PresentedValue value={content.reason} timeZone={timeZone} />
        )}
      </div>
    </div>
  );
};
