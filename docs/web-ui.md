# Web UI

The [workspace components](../web/src/ui/) own the interface. Page places the heading and primary
action before the operation feedback, view tabs, toolbar and content. Section keeps its heading,
count, secondary actions and body inside one card. The outcome banner takes focus and links to the
receipt after a change.

Field owns control labels, hints, rules and errors. Text, date, select and combobox controls share
the sizing and border tokens in [field.css](../web/src/ui/field.css). The control box draws the
only border and focus ring. Its inputs and date segments stay inside the box, and its inner button
sits at the inline end with a transparent background and zero border. Focused date parts use
the subtle accent pair or the system highlight pair in forced colours. MoneyField joins the amount
and currency under one border with an inline-start divider on the currency. Shared rows align
labels, controls and hints. A hint sits one `--space-1` gap under its control or option group.
Canonical typing rules appear on controls that accept the canonical form. The Filters
and export buttons wrap together. On phones, the search stays in the toolbar and Filters opens a
bottom sheet.

Core supplies chip tones, table prominence, column joins, recommendations, consequences and empty
wording. DataTables use wide rows from a 64rem container, medium rows from 50rem and cards at
smaller widths. The root reserves space for classic scrollbars, so short and long lists keep the
same width and opening a drawer keeps the page behind it still. The
[layout styles](../web/src/ui/layout.css) set table column measures of 10rem for identifiers,
14rem for text, 12rem for people, 9rem for money and 18rem for chips. Wide and medium cells have at least 12px
between contents and align on the first text line. Medium rows stack secondary values under the
primary cell. Chip lists wrap between chips in table rows. Card values flow in reading order.
A wrapped value and the value after it start their own lines. A card chip label wraps only when
its full width exceeds the line. In cards, monetary values start
at the inline edge, and rows with a menu reserve room for it at the inline end of the first line.
A recommendation sits below the values and the stack.

Text uses the workspace font. Identifiers use the mono font at the surrounding size and wrap first
at separators. Amounts break between digit groups or before the currency. A word can split when it
exceeds the width available to it. The browser matrix exercises maximum accepted values in the
[limits fixture](../web/e2e/design-matrix-fixtures.ts) at each layout boundary. Row controls stay
inside the list at every supported width. Text stays complete on screen. The phone app bar's
repeated title is the sole exception and uses a one-line ellipsis. The case breadcrumb shows
the full reference.

Subject facts use paired label and value tracks shared by each visual column. The grid has three
columns from 56rem, two from 35rem and one below. Each stage shows a dot, a decorative connector,
its label and any details. The paid stage retains its informative money meter.

A row recommendation uses a short core label on a secondary button. Each page and dialog allows
one solid primary action. Selected choices use the subtle accent and keep the radio indicator.

The cascade runs through reset, tokens, base, layout, components and views. The theme generator reads
[tokens.json](../web/src/theme/tokens.json). Keep authored styles in the declared layer and use logical
properties for inline and block spacing. Check keyboard focus, Arabic, both colour schemes, reduced
motion and forced colours.

L5 confines form controls to the UI components. L6 protects word wrapping. The browser matrix checks
control geometry, word integrity, primary emphasis, page offsets, minimum type size, disclosure
placement, page titles, first-row position and feedback focus. See [Verification](contributing.md#verification).
