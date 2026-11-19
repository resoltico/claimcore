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
  return content.notices.map((notice, index) => (
    <div key={index} className="region-notice" data-tone={notice.tone} role="status">
      <Icon name="check" />
      <div>
        <p>{renderCatalogKey(p, notice.messageId)}</p>
        {notice.reason === undefined ? null : (
          <PresentedValue value={notice.reason} timeZone={timeZone} />
        )}
      </div>
    </div>
  ));
};
