import { useId } from "react";
import type { PresentedMoneyMeter } from "@contracts/client";
import { SuppliedMessage } from "../SuppliedMessage";
import "./meter.css";

export const MoneyMeter = ({
  meter,
  timeZone,
}: {
  meter: PresentedMoneyMeter;
  timeZone: string;
}) => {
  const caption = useId();
  return (
    <div className="money-meter">
      <meter
        className="progress-meter"
        min={0}
        max={meter.maximum}
        value={meter.position}
        aria-labelledby={caption}
      />
      <p id={caption}>
        <SuppliedMessage message={meter.description} timeZone={timeZone} />
      </p>
    </div>
  );
};
