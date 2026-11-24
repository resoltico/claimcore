import { NoticeView } from "../presentation/Message";
import { DateEntryDraft } from "../components/DateEntryDraft";
import { I18nProvider } from "react-aria-components/I18nProvider";
import { FieldGroup, FieldRule } from "./Field";
import { parseDate } from "@internationalized/date";
import {
  DateField as AriaDateField,
  DateInput,
  DateSegment,
} from "react-aria-components/DateField";
import { Calendar, CalendarGrid, CalendarCell } from "react-aria-components/Calendar";
import { IconButton } from "./IconButton";
import { DialogTrigger, Dialog } from "react-aria-components/Dialog";
import { Heading } from "react-aria-components/Heading";
import { Popover } from "react-aria-components/ComboBox";
import { Label } from "react-aria-components/Label";
import { Text } from "react-aria-components/Text";
import { FieldError } from "react-aria-components/FieldError";
import type { FieldCatalogEntry } from "../protocol/types";
import type { Notice } from "../protocol/notices";
import { usePresentation } from "../presentation/context";
const dateValue = (value: string) => {
  try {
    return parseDate(value);
  } catch {
    return null;
  }
};
export type TypedFieldProps = {
  field: FieldCatalogEntry;
  value: string;
  inputName: string;
  disabled: boolean;
  optional?: boolean | undefined;
  error?: Notice | undefined;
  sharedErrorId?: string | undefined;
  onChange: (value: string) => void;
};
const CalendarPopup = ({ field, value, disabled, onChange }: TypedFieldProps) => {
  const p = usePresentation();
  return (
    <I18nProvider locale={p.language === "en-XA" ? "en" : p.language}>
      <DialogTrigger>
        <IconButton
          icon="calendar"
          variant="quiet"
          isDisabled={disabled}
          label={`${p.text("ui.calendar")}: ${p.fieldLabel(field.name)}`}
        />
        <Popover className="calendar-popover">
          <Dialog aria-label={p.fieldLabel(field.name)}>
            <I18nProvider locale={p.displayLocale}>
              <Calendar
                aria-label={p.fieldLabel(field.name)}
                value={dateValue(value)}
                onChange={(next) => {
                  onChange(next.toString());
                }}
              >
                <div className="calendar-heading">
                  <IconButton
                    slot="previous"
                    icon="chevron-start"
                    label={p.text("ui.previousMonth")}
                  />
                  <Heading lang={p.displayLocale} />
                  <IconButton slot="next" icon="chevron-end" label={p.text("ui.nextMonth")} />
                </div>
                <CalendarGrid lang={p.displayLocale}>
                  {(day) => <CalendarCell date={day} />}
                </CalendarGrid>
              </Calendar>
            </I18nProvider>
          </Dialog>
        </Popover>
      </DialogTrigger>
    </I18nProvider>
  );
};
const DateEntry = ({
  field,
  value,
  inputName,
  disabled,
  error,
  onChange,
  optional,
}: TypedFieldProps) => {
  const p = usePresentation();
  return (
    <FieldGroup>
      <AriaDateField
        id={`field-${inputName}`}
        name={inputName}
        className="text-field"
        data-field-name={field.name}
        value={dateValue(value)}
        onChange={(next) => {
          onChange(next?.toString() ?? "");
        }}
        isDisabled={disabled}
        isInvalid={error !== undefined}
        validationBehavior="aria"
      >
        <DateEntryDraft path={inputName} canonical={value} disabled={disabled} />
        <div className="field-label-row">
          <Label>{p.fieldLabel(field.name)}</Label>
          {optional === true ? (
            <span className="field-hint">{p.text("label.optional")}</span>
          ) : null}
        </div>
        <div className="control-box field-control-group">
          <DateInput lang={p.displayLocale} aria-invalid={error !== undefined || undefined}>
            {(segment) => <DateSegment segment={segment} />}
          </DateInput>
          <CalendarPopup
            field={field}
            value={value}
            inputName={inputName}
            disabled={disabled}
            error={error}
            onChange={onChange}
          />
        </div>
        <Text slot="description" className="field-description">
          <span className="field-hint">{p.hint(field)}</span>{" "}
          <FieldRule name={field.name} ruleId={field.ruleId} invalid={error !== undefined} />
        </Text>
        {error === undefined ? null : (
          <FieldError>
            <NoticeView value={error} />
          </FieldError>
        )}
      </AriaDateField>
    </FieldGroup>
  );
};
export const DateField = (props: TypedFieldProps) => {
  const p = usePresentation();
  return (
    <I18nProvider locale={p.displayLocale}>
      <DateEntry {...props} />
    </I18nProvider>
  );
};
