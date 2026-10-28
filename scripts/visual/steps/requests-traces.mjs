// Web steps for the Requests and Traces scenes (tests/Astra.Desktop.UiTests/Scenes.Requests.cs, Scenes.Traces.cs).
// See scripts/visual/web-steps.mjs for the entry shape and helpers.
//
// Requests: the desktop opens a detail with page.Open(id) and expands raw section N (ExpandedRawIndex); the web has no route
// for a detail, so the step clicks the request's row (the fixture list is newest first: req_0050 is row 0, req_00NN is row
// 50-NN) and then the N-th accordion header of the sheet's raw list (usage raw, pricing snapshot, client request body, …).
// The desktop's filters come in as the route param (the web's own query string), so those scenes open /requests?<param>.
// Traces: the desktop param IS the web path (/traces/sessions/cc%3Aabc …), so those scenes set `path` to it; the 详情 switch
// is the shared localStorage key astra.traces.inspector (TraceInspectorSwitch.Set on the desktop); a node is selected by
// clicking the React Flow node whose data-id is the id the desktop's Select(...) takes.

/** Clicks the row of `id` in the request table (default fixture order: req_0050 first). */
async function openRow(page, id) {
  const index = 50 - Number(id.replace(/^req_0*/, ''));
  const rows = page.locator('table tbody tr').filter({ visible: true });
  await rows.nth(index).locator('button').first().click({ timeout: 5000 }); // the model cell's button opens the sheet
  await page.getByRole('dialog').filter({ visible: true }).first().waitFor({ timeout: 5000 });
  await page.waitForTimeout(700);
}

/** Expands the n-th raw accordion item of the open request sheet. */
async function expandRaw(page, n) {
  const dialog = page.getByRole('dialog').filter({ visible: true }).first();
  const headers = dialog.locator('button[aria-expanded]');
  await headers.nth(n).scrollIntoViewIfNeeded();
  await headers.nth(n).click({ timeout: 5000 });
  await page.waitForTimeout(600);
  // the desktop sets ExpandedRawIndex without scrolling the sheet: its frame shows the sheet's top, so put the web one back there
  await page.evaluate(() => {
    const d = [...document.querySelectorAll('[role=dialog]')].find((e) => e.getClientRects().length > 0);
    if (!d) return;
    for (const e of [d, ...d.querySelectorAll('*')]) if (e.scrollTop > 0) e.scrollTop = 0;
  });
  await page.waitForTimeout(300);
}

/** The desktop scene opens the sheet on a request that is not in the list (req_live): the web can only open a row, so the
 *  first row of the recorded list is given that id (the list behind the sheet looks the same). */
function renameFirstRow(id) {
  return (rec) => {
    for (const c of rec.calls) {
      if (c.method !== 'GET' || !c.url.startsWith('/api/requests?') || !c.body) continue;
      const list = JSON.parse(c.body);
      if (list.items?.length) list.items[0].id = id;
      c.body = JSON.stringify(list);
    }
    return rec;
  };
}

const detail = (id, raw = -1, extra = {}) => ({
  ...extra,
  run: async (page) => {
    await openRow(page, extra.rowId ?? id);
    if (raw >= 0) await expandRaw(page, raw);
  },
});

/** Selects a React Flow node by its id. */
async function selectNode(page, id) {
  const node = page.locator(`.react-flow__node[data-id="${id}"]`).first();
  await node.waitFor({ timeout: 5000 });
  await node.click({ timeout: 5000, force: true });
  await page.waitForTimeout(500);
}

/** capture-web answers a GET from the scene's override before it looks for a recorded hang; a scene whose overridden path
 *  hung on the desktop (the loading scenes) drops the override so the hang is replayed. */
function hangOverrides(rec) {
  for (const c of rec.calls) if (c.hang) delete rec.overrides?.[new URL(c.url, 'http://x').pathname];
  return rec;
}

const inspectorOff = { 'astra.traces.inspector': '0' };
const inspectorOn = { 'astra.traces.inspector': '1' };

export const STEPS = {
  // ----- requests -----
  'requests__data': {},
  'requests__empty': {},
  'requests__loading': { settle: 600, patch: hangOverrides },
  'requests__error': { settle: 2500 },
  'requests__filter-active': { path: '/requests?status=success&model=gpt' },
  'requests__filter-empty': { path: '/requests?token=tok_ci&model=nothing' },
  'requests__paged': {},
  'requests__remote': {},
  'requests__live-on': { settle: 2000 },
  'requests__live-off': {},
  'requests__detail-ok': detail('req_0049'),
  'requests__detail-error': detail('req_0041'),
  'requests__detail-streaming': { patch: renameFirstRow('req_live'), ...detail('req_live', -1, { rowId: 'req_0050' }) },
  'requests__detail-routing': detail('req_0049'),
  'requests__detail-fallback': detail('req_0049'),
  'requests__body-expand': detail('req_0049', 2),
  'requests__body-truncated': detail('req_0049', 2),

  // ----- traces -----
  'traces__topology': { storage: inspectorOff, settle: 2000 },
  'traces__topology-idle': { storage: inspectorOff },
  'traces__topology-inspector': { storage: inspectorOn, run: (page) => selectNode(page, 'provider:prov_anthropic') },
  'traces__empty': { storage: inspectorOff },
  'traces__error': { storage: inspectorOff, settle: 2500 },
  'traces__unsupported': { storage: inspectorOff, settle: 2500 },
  'traces__loading': { storage: inspectorOff, settle: 600, patch: hangOverrides },
  'traces__sessions': { storage: inspectorOff, path: '/traces/sessions' },
  'traces__sessions-empty': { storage: inspectorOff, path: '/traces/sessions' },
  'traces__session-canvas': { storage: inspectorOff, path: '/traces/sessions/cc%3Aabc' },
  'traces__session-inspector': { storage: inspectorOn, path: '/traces/sessions/cc%3Aabc', run: (page) => selectNode(page, 'm3') },
  'traces__session-timeline': {
    storage: inspectorOff,
    path: '/traces/sessions/cc%3Aabc?view=timeline',
    // SelectRow("m3"): the timeline row of call m3 (the main call that was retried: its label carries the retry flag)
    run: async (page) => {
      const rows = page.locator('section button[aria-pressed]');
      await rows.first().waitFor({ timeout: 5000 });
      const n = await rows.count();
      for (let i = 0; i < n; i++) {
        const key = await rows.nth(i).evaluate((el) => {
          const k = Object.keys(el).find((x) => x.startsWith('__reactFiber$'));
          return k ? el[k].key : null;
        });
        if (key === 'm3' || key === 'call:m3') {
          await rows.nth(i).click();
          break;
        }
      }
      await page.waitForTimeout(400);
    },
  },
  'traces__session-live': { storage: inspectorOff, path: '/traces/sessions/cc%3Aabc', settle: 2000 },
  'traces__requests': { storage: inspectorOff, path: '/traces/requests' },
  'traces__request': { storage: inspectorOff, path: '/traces/requests/req_trace' },
  'traces__request-inspector': { storage: inspectorOn, path: '/traces/requests/req_trace', run: (page) => selectNode(page, 'attempt:0') },
  'traces__request-inflight': { storage: inspectorOff, path: '/traces/requests/req_trace' },
  'traces__request-notfound': { storage: inspectorOff, path: '/traces/requests/req_gone', settle: 2500 },
};
export const DESKTOP_ONLY = {};
export const NOT_MIRRORED = {};
