---
name: daily-bill-styles
description: the overview's daily bill offers two looks — receipt (A) and comic page (E) — picked by the user from five mockups; web and desktop draw the same picture, exported without a DOM-to-image library
type: project
updated: 2026-11-11
source: session
evidence: web/src/components/bill/ (BillArt.tsx, bill.css, exportImage.ts), web/src/lib/bill.ts, src/Astra.Desktop.Core/Overview/DailyBillShaping.cs, GET /api/stats/daily-bill
---

On 2026-11-11 the user asked for a one-click "today's bill" (per provider → model, with an Astra Gate mark) and compared
five mockups: A thermal receipt, B minimal invoice, C console-native panel, D dark 9:16 share card, E comic/line-art page
(matching the line-art logo). They chose to offer **A and E** as a per-user style switch, and asked that the web console
and the Avalonia desktop client stay consistent.

How it holds together: the server returns the day already grouped and sorted (`DailyBillDto`); the text is shaped by
`web/src/lib/bill.ts` and its C# port `DailyBillShaping`, whose tests pin the same strings; the looks are `bill.css` and its
twins `ReceiptBillView` / `MangaBillView`; the visual scenes `overview__bill-receipt` / `overview__bill-manga` compare the two.
The web PNG export is the bill's markup + `bill.css` inside an SVG `<foreignObject>` (a `pnpm add html-to-image` hung with no
network on that day, and the self-contained stylesheet made a library unnecessary), so `bill.css` must not depend on the
console's base styles. The bill uses system fonts only (the console runs offline; no web fonts).

**Why:** a bill is shared as an image, so both clients must produce the same picture from the same numbers.

**How to apply:** a change to a bill's text or look touches both clients (lib/bill.ts ↔ DailyBillShaping.cs with their tests,
bill.css ↔ the two Avalonia views) and the scenes; a third style is a new `BillStyle` value on both sides plus zh/en
`bill.style.<id>`. B, C and D exist only as the mockup (session scratchpad, not in the repo).
