/* ===========================================================================
   Extract console - front end
   No framework and no build step: the app ships as a single self-contained exe
   and there is no Node on the machines this runs from.
   ======================================================================== */

'use strict';

// ---- plumbing -------------------------------------------------------------

const PARAMS = new URLSearchParams(location.search);
const TOKEN = PARAMS.get('t') || '';
const WANT_ENV = PARAMS.get('env');

// Keep the token out of the address bar once we have it - it is a credential.
// The hash carries the selected tab and ?env= the environment, so both survive.
if (TOKEN) {
  const keep = WANT_ENV ? `?env=${encodeURIComponent(WANT_ENV)}` : '';
  history.replaceState(null, '', location.pathname + keep + location.hash);
}

const state = {
  env: null,
  date: todayIso(),
  historyTop: 40,
  pollSeconds: 15,
  actionsEnabled: true,
  timer: null,
  openStep: null,
  reconExact: false,
  reconRecords: false,
  tab: 'history',
  busy: false,
  dateTouched: false,
  diagFilter: 'all',
};

async function api(path, options = {}) {
  const res = await fetch(path, {
    ...options,
    headers: {
      'X-Console-Token': TOKEN,
      ...(options.body ? { 'Content-Type': 'application/json' } : {}),
      ...(options.headers || {}),
    },
  });
  if (res.status === 401) throw new Error('Console token rejected. Close this tab and relaunch ExtractConsole.exe.');
  if (res.status === 403) {
    // The server says why: the role, or the account not being in any allowed group.
    let msg = 'Not available to your role.';
    try { msg = (await res.json()).error || msg; } catch { /* keep the default */ }
    throw new Error(msg);
  }
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json();
}

const $ = (sel) => document.querySelector(sel);
const el = (tag, cls, text) => {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text !== undefined) n.textContent = text;
  return n;
};

// ---- formatting -----------------------------------------------------------

function todayIso(d = new Date()) {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

function fmtDuration(seconds) {
  if (seconds === null || seconds === undefined) return '—';
  const s = Math.max(0, Math.round(seconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  if (h > 0) return `${h}h ${String(m).padStart(2, '0')}m`;
  if (m > 0) return `${m}m ${String(sec).padStart(2, '0')}s`;
  return `${sec}s`;
}

function fmtDateTime(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleString([], {
    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false,
  });
}

function fmtNumber(n) {
  return n === null || n === undefined ? '—' : n.toLocaleString();
}

function fmtBytes(b) {
  if (b === null || b === undefined) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  let i = 0, v = b;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i += 1; }
  return `${v >= 10 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}`;
}

// Status is never colour alone: each gets a glyph and a word.
const STATUS = {
  succeeded: { glyph: '✓', label: 'Succeeded', colorVar: '--good' },
  running:   { glyph: '▶', label: 'Running',   colorVar: '--warning' },
  failed:    { glyph: '✕', label: 'Failed',    colorVar: '--critical' },
  // Canceled takes the "serious" token, not "critical": a run someone stopped is a
  // different thing from one that broke, and two identical red swatches in the legend
  // make the chart unreadable.
  canceled:  { glyph: '■', label: 'Canceled',  colorVar: '--serious' },
  retry:     { glyph: '↻', label: 'Retrying',  colorVar: '--serious' },
  noop:      { glyph: '—', label: 'Disabled',  colorVar: '--idle' },
  // Reconciliation only: more lines than rows, which is a counting artifact rather than
  // missing data. Warning rather than critical, so it does not read as a load failure.
  artifact:  { glyph: '!',      label: 'Extra lines', colorVar: '--warning' },
  // Reconciliation only: table and file agree AND both are empty. Critical, not green -
  // "nothing matches nothing" is the signature of an extract that produced no data.
  empty:     { glyph: '∅', label: 'No rows',     colorVar: '--critical' },
  pending:   { glyph: '·', label: 'Pending',   colorVar: '--idle' },
  skipped:   { glyph: '·', label: 'Not reached', colorVar: '--idle' },
  unknown:   { glyph: '?',      label: 'Unknown',   colorVar: '--idle' },
};

function statusEl(code, overrideLabel) {
  const s = STATUS[code] || STATUS.unknown;
  const wrap = el('span', `status ${code}`);
  wrap.append(el('span', 'glyph', s.glyph), el('span', 'label', overrideLabel || s.label));
  return wrap;
}

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

// ---- toast / banner / dialog ---------------------------------------------

function toast(message, ok = true) {
  const t = el('div', `toast ${ok ? 'ok' : 'bad'}`, message);
  $('#toasts').appendChild(t);
  setTimeout(() => t.remove(), ok ? 5000 : 9000);
}

function setBanners(list) {
  const host = $('#banners');
  host.replaceChildren();
  for (const b of list) {
    const n = el('div', `banner${b.severe ? ' err' : ''}`);
    n.append(el('span', null, b.text));
    host.appendChild(n);
  }
}

function confirmDialog({ title, body, typeToConfirm, typeLabel }) {
  return new Promise((resolve) => {
    const dlg = $('#confirmDlg');
    $('#dlgTitle').textContent = title;
    $('#dlgBody').textContent = body;

    const wrap = $('#dlgTypeWrap');
    const input = $('#dlgType');
    $('#dlgTypePrompt').textContent = typeLabel || `Type ${typeToConfirm} to confirm:`;
    wrap.hidden = !typeToConfirm;
    input.value = '';

    const ok = $('#dlgOk');
    const check = () => { ok.disabled = typeToConfirm ? input.value.trim() !== typeToConfirm : false; };
    check();
    input.oninput = check;

    const done = (value) => {
      dlg.close();
      ok.onclick = null; $('#dlgCancel').onclick = null; input.oninput = null;
      resolve(value);
    };
    ok.onclick = () => done(typeToConfirm ? input.value.trim() : true);
    $('#dlgCancel').onclick = () => done(null);

    dlg.showModal();
    if (typeToConfirm) input.focus();
  });
}

// ---- bootstrap ------------------------------------------------------------

async function boot() {
  let info;
  try {
    info = await api('/api/bootstrap');
  } catch (e) {
    setBanners([{ text: e.message, severe: true }]);
    return;
  }

  state.pollSeconds = info.pollSeconds;
  state.actionsEnabled = info.actionsEnabled;
  state.access = info.access;

  $('#whoami').textContent =
    `${info.access.user} · ${info.access.role} · ${info.mode === 'shared' ? 'shared' : 'local'} mode`;
  $('#whoami').title = info.access.grantedBy ? `Role granted by ${info.access.grantedBy}` : '';

  // Not in any allowed group: say so plainly and show nothing else. Every API call would
  // be refused anyway; this saves the page from a wall of errors.
  if (info.access.roleKey === 'none') {
    setBanners([{
      text: `${info.access.user} is not in any group allowed to use this console. `
          + 'Ask the data engineering team for access.',
      severe: true,
    }]);
    for (const n of document.querySelectorAll('.filters, #heroCard, #stepsCard, .tabs, .tabpane')) n.hidden = true;
    return;
  }
  applyRole(info.access);
  $('#diagLocation').textContent = info.diagnosticsLocation || 'diag.FailureRule';

  const sel = $('#envSel');
  sel.replaceChildren();
  for (const e of info.environments) {
    const opt = el('option', null, e.enabled ? e.name : `${e.name} (not configured)`);
    opt.value = e.name;
    opt.disabled = !e.enabled;
    sel.appendChild(opt);
  }
  const def =
    (WANT_ENV && info.environments.find(
      (e) => e.enabled && e.name.toLowerCase() === WANT_ENV.toLowerCase()))
    || info.environments.find((e) => e.isDefault && e.enabled)
    || info.environments.find((e) => e.enabled);
  if (!def) { setBanners([{ text: 'No environment in appsettings.json is enabled.', severe: true }]); return; }

  state.env = def.name;
  sel.value = def.name;
  // One environment to choose from is no choice: show it, but not as a control.
  sel.disabled = info.environments.filter((e) => e.enabled).length <= 1;
  applyEnvChrome(info, def.name);

  $('#dateSel').value = state.date;

  wireControls(info);

  // #recon, #files, ... deep-links straight to a tab and survives a reload - but only to a
  // tab this role has. Otherwise open on the role's first tab.
  const wanted = location.hash.replace('#', '');
  if (wanted && mayUseTab(wanted)) selectTab(wanted);
  else if (!mayUseTab(state.tab)) selectTab(state.access.tabs[0]);

  await refreshAll();
  startPolling();
}

function mayUseTab(name) {
  return !!state.access && state.access.tabs.includes(name) && !!document.getElementById(`tab-${name}`);
}

/**
 * Shapes the page to the role. Cosmetic only: the server refuses anything the role may
 * not do, whatever is on screen. This just stops people being offered it.
 */
function applyRole(a) {
  for (const btn of document.querySelectorAll('.tabs button')) btn.hidden = !a.tabs.includes(btn.dataset.tab);
  for (const pane of document.querySelectorAll('.tabpane')) {
    if (!a.tabs.includes(pane.id.replace(/^tab-/, ''))) pane.hidden = true;
  }
  // Finance asked for its four tabs and nothing above them.
  $('#heroCard').hidden = !a.statusPanel;
  $('#stepsCard').hidden = !a.statusPanel;
  // A role that may only look is not shown the controls for changing a list.
  $('#adhocChangeCol').hidden = a.roleKey !== 'operator';
}


/** Operator in a console started read-only still sees the controls, disabled. Other roles do not. */
function roleMayChange() {
  return !!state.access && state.access.roleKey === 'operator';
}

function applyEnvChrome(info, envName) {
  const e = info.environments.find((x) => x.name === envName);
  if (!e) return;
  $('#brandSub').textContent = `${e.jobName} · ${e.controlServer}`;
  $('#shareHint').textContent = e.fileShare || '(no share configured)';
  $('#archiveHint').textContent = e.archiveShare || '(no archive share configured)';
  $('#upsertLocation').textContent = e.upsertLogLocation || 'DailyReconCurrentProcessLog';
  $('#adhocShare').textContent = e.fileShare || '(no share configured)';
  $('#volLocation').textContent = e.volumeLogLocation || 'etl_audit_log';
  state.envInfo = e;

  const banners = [];
  if (e.isProduction) {
    banners.push({
      text: roleMayChange()
        ? `PRODUCTION — ${e.controlServer}. Starting or stopping here affects the live daily extract.`
        : `PRODUCTION — ${e.controlServer}.`,
      severe: true,
    });
  }
  if (!state.actionsEnabled) {
    banners.push({
      text: roleMayChange()
        ? 'Read-only: this console was started without --allow-actions.'
        : `View only: your account has the ${state.access.role} role, so nothing here can be started, stopped or changed.`,
      severe: false,
    });
  }
  setBanners(banners);
  document.documentElement.classList.toggle('is-prod', !!e.isProduction);
}

function wireControls(info) {
  wireAdhoc();
  wireVolume();
  $('#envSel').onchange = (ev) => {
    state.env = ev.target.value;
    state.openStep = null;
    // Drop the upsert day: the environments do not record the same dates, so carrying
    // DEV's choice to PROD asked for a day PROD has never had and showed an empty table.
    upsertDay = null;
    // And never carry a half-prepared AdHoc change into another environment - an entry
    // previewed for DEV must not be one click from landing in PROD.
    resetAdhocForm();
    adhoc.key = null;
    applyEnvChrome(info, state.env);
    refreshAll();
  };
  $('#dateSel').onchange = (ev) => {
    state.date = ev.target.value;
    state.dateTouched = true;
    refreshRecon();
    refreshFiles();
  };
  $('#histSel').onchange = (ev) => { state.historyTop = Number(ev.target.value); refreshHistory(); };
  $('#refreshBtn').onclick = () => refreshAll();
  $('#autoChk').onchange = (ev) => (ev.target.checked ? startPolling() : stopPolling());
  $('#exactBtn').onclick = () => { state.reconExact = true; refreshRecon(); };
  $('#recordsBtn').onclick = () => { state.reconRecords = true; refreshRecon(); };

  $('#themeBtn').onclick = () => {
    const cur = document.documentElement.getAttribute('data-theme');
    const next = cur === 'dark' ? 'light' : cur === 'light' ? '' : 'dark';
    if (next) document.documentElement.setAttribute('data-theme', next);
    else document.documentElement.removeAttribute('data-theme');
    localStorage.setItem('ec-theme', next);
    refreshHistory();          // chart colours are read from CSS vars
  };
  const saved = localStorage.getItem('ec-theme');
  if (saved) document.documentElement.setAttribute('data-theme', saved);

  for (const btn of document.querySelectorAll('.tabs button')) {
    btn.onclick = () => selectTab(btn.dataset.tab);
  }
}

function selectTab(name) {
  if (!mayUseTab(name)) return;
  state.tab = name;
  if (location.hash !== `#${name}`) history.replaceState(null, '', `#${name}`);
  for (const btn of document.querySelectorAll('.tabs button')) {
    btn.setAttribute('aria-selected', String(btn.dataset.tab === name));
  }
  for (const pane of document.querySelectorAll('.tabpane')) {
    pane.hidden = pane.id !== `tab-${name}`;
  }
  if (name === 'ssis') refreshSsis();
  if (name === 'diagnostics') refreshDiagnostics();
  if (name === 'recon') refreshRecon();
  if (name === 'files') refreshFiles();
  if (name === 'history') refreshHistory();
  if (name === 'upserts') refreshUpserts();
  if (name === 'adhoc') refreshAdhoc();
  if (name === 'volume') refreshVolume();
}

function startPolling() {
  stopPolling();
  // A role without the status panel follows the run history instead, less often.
  const statusPanel = !state.access || state.access.statusPanel;
  const tick = statusPanel ? refreshStatus : refreshHistory;
  const every = statusPanel ? state.pollSeconds : Math.max(60, state.pollSeconds);
  state.timer = setInterval(() => { if (!document.hidden) tick(); }, every * 1000);
}
function stopPolling() {
  if (state.timer) clearInterval(state.timer);
  state.timer = null;
}

async function refreshAll() {
  const statusPanel = !state.access || state.access.statusPanel;
  await Promise.all([statusPanel ? refreshStatus() : Promise.resolve(), refreshHistory()]);
  if (state.tab === 'recon') refreshRecon();
  if (state.tab === 'files') refreshFiles();
  if (state.tab === 'ssis') refreshSsis();
  if (state.tab === 'diagnostics') refreshDiagnostics();
  if (state.tab === 'upserts') refreshUpserts();
  if (state.tab === 'adhoc') refreshAdhoc();
  if (state.tab === 'volume') refreshVolume();
}

function stamp() {
  $('#lastRefresh').textContent = `updated ${new Date().toLocaleTimeString([], { hour12: false })}`;
}

/** Holds the previous render at reduced opacity instead of flashing a skeleton. */
function dim(node, on) {
  if (node) node.classList.toggle('refreshing', on);
}

function panelError(node, message) {
  node.replaceChildren(el('div', 'panel-error', message));
}

// ---- status + steps -------------------------------------------------------

async function refreshStatus() {
  const hero = $('#heroBody');
  const steps = $('#stepsBody');
  dim(hero, true); dim(steps, true);

  let panel;
  try {
    panel = await api(`/api/status?env=${encodeURIComponent(state.env)}`);
  } catch (e) {
    panelError(hero, e.message); steps.replaceChildren();
    dim(hero, false); dim(steps, false);
    return;
  }

  dim(hero, false); dim(steps, false);
  stamp();

  if (panel.error) { panelError(hero, panel.error); steps.replaceChildren(); return; }
  state.isRunning = !!panel.data.isRunning;
  renderHero(panel.data);
  renderSteps(panel.data);
  syncDateToLastRun(panel.data);
  renderAdhocRunning();
}

/**
 * The extract runs ad hoc, not every day, so "today" is usually the wrong date to
 * reconcile against - it would open on a wall of "not found". Follow the last run
 * until the operator picks a date themselves.
 */
function syncDateToLastRun(s) {
  if (!s.startedLocal) return;
  const iso = todayIso(new Date(s.startedLocal));
  // Recorded even when the operator has picked their own date, so the reconciliation
  // panel can warn that the ODS tables no longer correspond to it.
  state.lastRunDate = iso;
  if (state.dateTouched) return;
  if (iso === state.date) return;

  state.date = iso;
  $('#dateSel').value = iso;
  state.lastRunDate = iso;
  if (state.tab === 'recon') refreshRecon();
  if (state.tab === 'files') refreshFiles();
}

function renderHero(s) {
  const host = $('#heroBody');
  const wrap = el('div', 'hero');

  const main = el('div', 'hero-main');

  const figure = el('div', 'hero-figure');
  const st = STATUS[s.outcome] || STATUS.unknown;
  if (s.isRunning) {
    // Only claim a step number when we actually have one; "step ?" told the reader nothing.
    figure.textContent = s.currentStepId
      ? `Running · step ${s.currentStepId} of ${s.steps.length}`
      : 'Running';
  } else {
    figure.textContent = st.label;
  }
  figure.style.color = `var(${st.colorVar})`;
  main.appendChild(figure);

  const subBits = [];
  if (s.isRunning) {
    const cur = s.steps.find((x) => x.stepId === s.currentStepId);
    if (cur) subBits.push(cur.stepName);
    subBits.push(`elapsed ${fmtDuration(s.elapsedSeconds)}`);
  } else if (s.outcome === 'failed' && s.failedStepId) {
    const f = s.steps.find((x) => x.stepId === s.failedStepId);
    subBits.push(`broke at step ${s.failedStepId}${f ? ` · ${f.stepName}` : ''}`);
  }
  if (subBits.length) main.appendChild(el('div', 'hero-sub', subBits.join(' · ')));

  const stats = el('div', 'hero-stats');
  const addStat = (k, v) => {
    const n = el('div', 'stat');
    n.append(el('div', 'k', k), el('div', 'v', v));
    stats.appendChild(n);
  };
  addStat('Started', fmtDateTime(s.startedLocal));
  addStat(s.isRunning ? 'Elapsed' : 'Duration', fmtDuration(s.isRunning ? s.elapsedSeconds : s.durationSeconds));
  addStat('Invoked by', s.invokedBy || '—');
  addStat('Next scheduled', s.nextScheduledLocal ? fmtDateTime(s.nextScheduledLocal) : 'not scheduled');
  main.appendChild(stats);

  if (!s.jobEnabled) {
    main.appendChild(el('div', 'note', 'This job is disabled in SQL Agent - schedules will not fire it.'));
  }

  // ---- actions
  const actions = el('div', 'hero-actions');

  const runBtn = el('button', 'primary', 'Run from the beginning');
  runBtn.disabled = !state.actionsEnabled || s.isRunning || state.busy;
  runBtn.onclick = () => doStart(null, null);
  actions.appendChild(runBtn);

  if (s.outcome === 'failed' && s.failedStepId) {
    const f = s.steps.find((x) => x.stepId === s.failedStepId);
    const restart = el('button', null, `Restart from step ${s.failedStepId}`);
    restart.disabled = !state.actionsEnabled || s.isRunning || state.busy;
    restart.onclick = () => doStart(s.failedStepId, f ? f.stepName : null);
    actions.appendChild(restart);
  }

  const stopBtn = el('button', 'danger', 'Stop the run');
  stopBtn.disabled = !state.actionsEnabled || !s.isRunning || state.busy;
  stopBtn.onclick = () => doStop(s.jobName);
  actions.appendChild(stopBtn);

  // A role that can only look gets the state, not the controls.
  if (roleMayChange()) wrap.append(main, actions);
  else wrap.append(main);
  host.replaceChildren(wrap);
}

function renderSteps(s) {
  const host = $('#stepsBody');
  host.replaceChildren();

  for (const st of s.steps) {
    const row = el('div', 'step clickable');
    // Highlight the in-flight step only while it really is in flight: a row showing a
    // finished outcome must never also be painted as current.
    if (s.isRunning && st.stepId === s.currentStepId && st.outcome === 'running') {
      row.classList.add('is-current');
    }

    row.append(el('div', 'num', String(st.stepId)));

    const name = el('div', 'name');
    name.append(document.createTextNode(st.stepName));
    name.appendChild(el('span', 'sub', st.subsystem));
    // Shown from the step definition, so it is visible even on a run that never got here.
    if (st.isNoOp) {
      const flag = el('span', 'flag', 'body commented out');
      flag.title = st.disabledNote
        || 'This step’s T-SQL is commented out, so it reports success without doing anything.';
      name.appendChild(flag);
    }
    if (!st.reachableFromStart) {
      const flag = el('span', 'flag', 'never runs on a normal pass');
      flag.title = 'An earlier step quits reporting success, so the job ends before reaching this one. '
                 + 'It only runs if you start the job at this step.';
      name.appendChild(flag);
    }
    if (st.configWarning) {
      const flag = el('span', 'flag warn', 'config mismatch');
      flag.title = st.configWarning;
      name.appendChild(flag);
    }
    row.appendChild(name);

    row.appendChild(el('div', 'dur', fmtDuration(st.durationSeconds)));
    // An unreachable step was never going to run, so "not reached" would imply a problem.
    row.appendChild(st.reachableFromStart
      ? statusEl(st.outcome)
      : statusEl(st.outcome === 'skipped' ? 'noop' : st.outcome,
                 st.outcome === 'skipped' ? 'Not in flow' : undefined));

    const detailHost = el('div');
    const toggle = () => {
      const open = state.openStep === st.stepId;
      state.openStep = open ? null : st.stepId;
      detailHost.replaceChildren(open ? '' : buildStepDetail(st));
    };
    row.onclick = toggle;
    if (state.openStep === st.stepId) detailHost.replaceChildren(buildStepDetail(st));

    host.append(row, detailHost);
  }
}

function buildStepDetail(st) {
  const d = el('div', 'step-detail');

  if (st.disabledNote) {
    d.appendChild(el('div', 'note', st.disabledNote));
  }
  if (st.isNoOp && !st.disabledNote) {
    d.appendChild(el('div', 'note',
      'This step’s body is commented out, so it reports success without doing any work.'));
  }
  // A commented-out step cannot have started its child jobs, so whatever state they are
  // in came from somewhere else. Say so before listing them.
  if (st.isNoOp && (st.childJobs || []).length) {
    d.appendChild(el('div', 'note warn',
      'Because this step is commented out it did NOT launch the job(s) below. '
      + 'Their status is from whenever they last ran by some other trigger.'));
  }
  if (st.configWarning) {
    d.appendChild(el('div', 'note warn', st.configWarning));
  }

  if (st.childJobs && st.childJobs.length) {
    const kids = el('div', 'children');
    for (const c of st.childJobs) {
      const row = el('div', 'child');
      row.appendChild(statusEl(c.outcome));
      row.appendChild(el('div', 'cname', c.jobName));

      const meta = [];
      if (c.startedLocal) meta.push(fmtDateTime(c.startedLocal));
      if (c.durationSeconds !== null && c.durationSeconds !== undefined) meta.push(fmtDuration(c.durationSeconds));
      if (c.failedStepId) meta.push(`failed at step ${c.failedStepId}: ${c.failedStepName || ''}`);
      if (!c.jobEnabled) meta.push('disabled in Agent');
      row.appendChild(el('div', 'cmeta', meta.join('  ·  ')));

      if (state.actionsEnabled) {
        const spacer = el('div'); spacer.style.flex = '1';
        row.appendChild(spacer);
        const b = el('button', 'ghost', 'Run');
        b.disabled = c.isRunning || state.busy;
        b.onclick = (ev) => { ev.stopPropagation(); doStart(null, null, c.jobName); };
        row.appendChild(b);
      }
      kids.appendChild(row);
    }
    d.appendChild(kids);
  }

  // The diagnosis goes ABOVE the raw error: the explanation is what the operator needs,
  // the Agent message is the evidence behind it.
  if (st.diagnosis) {
    const dx = el('div', 'diagnosis');
    const head = el('div', 'dx-title');
    head.append(el('span', 'dx-badge', 'Known issue'), document.createTextNode(st.diagnosis.title));
    dx.appendChild(head);
    if (st.diagnosis.cause) dx.appendChild(el('div', 'dx-body', st.diagnosis.cause));
    dx.appendChild(buildSolutions(st.diagnosis.solutions, st.diagnosis.ruleId));
    dx.appendChild(el('div', 'sub', `Catalogue rule #${st.diagnosis.ruleId}`));
    d.appendChild(dx);
  } else if (st.outcome === 'failed' && st.message && state.actionsEnabled) {
    const none = el('div', 'note');
    none.append(document.createTextNode('No catalogue rule explains this error yet. '));
    const b = el('button', 'ghost', 'Add one');
    b.onclick = (ev) => {
      ev.stopPropagation();
      openRuleDialog({ jobName: state.envInfo.jobName, stepId: st.stepId, stepName: st.stepName, message: st.message });
    };
    none.appendChild(b);
    d.appendChild(none);
  }

  if (st.message) {
    d.appendChild(el('pre', null, st.message));
  } else if (!st.disabledNote && !(st.childJobs || []).length) {
    d.appendChild(el('div', 'empty', 'No message recorded for this step in the current run.'));
  }

  if (st.retriesAttempted > 0) {
    d.appendChild(el('div', 'note', `${st.retriesAttempted} retry attempt(s) were made.`));
  }

  if (state.actionsEnabled) {
    const row = el('div', 'btn-row');
    row.style.marginTop = '12px';
    const b = el('button', null, `Restart the job from step ${st.stepId}`);
    b.disabled = state.busy;
    b.onclick = (ev) => { ev.stopPropagation(); doStart(st.stepId, st.stepName); };
    row.appendChild(b);
    d.appendChild(row);
  }

  // Clicks inside the detail must not re-toggle the row.
  d.onclick = (ev) => ev.stopPropagation();
  return d;
}

// ---- actions --------------------------------------------------------------

async function doStart(fromStepId, stepName, jobName) {
  const target = jobName || state.envInfo.jobName;
  const prod = state.envInfo.isProduction;
  const where = `${state.env} (${state.envInfo.controlServer})`;

  let body = fromStepId
    ? `Agent will start "${target}" on ${where} at step ${fromStepId} (${stepName}) and continue to the end. `
      + `Steps 1 to ${fromStepId - 1} will NOT run - including the start e-mail.`
    : `Agent will run "${target}" on ${where} from step 1. This job has taken several hours to complete.`;

  if (prod) body = `This is PRODUCTION.\n\n${body}`;

  const ok = await confirmDialog({
    title: fromStepId ? `Restart from step ${fromStepId}?` : 'Run the extract?',
    body,
    // On production, make the operator name the environment before anything starts.
    typeToConfirm: prod ? state.env : null,
  });
  if (!ok) return;

  state.busy = true;
  try {
    const r = await api('/api/actions/start', {
      method: 'POST',
      body: JSON.stringify({
        environment: state.env,
        jobName: jobName || null,
        fromStepId: fromStepId || null,
        confirm: prod ? ok : null,      // the server re-checks this on production
      }),
    });
    toast(r.message, r.success);
  } catch (e) {
    toast(e.message, false);
  } finally {
    state.busy = false;
    setTimeout(refreshStatus, 1200);
  }
}

async function doStop(jobName) {
  const typed = await confirmDialog({
    title: 'Stop the running extract?',
    body: 'Stopping mid-run can leave partial data in the staging and ODS tables, and files on the share '
        + 'may be incomplete. Agent stops the job once the running step yields - a long-running step may take a while.',
    typeToConfirm: jobName,
    typeLabel: 'Type the job name to confirm:',
  });
  if (!typed) return;

  state.busy = true;
  try {
    const r = await api('/api/actions/stop', {
      method: 'POST',
      body: JSON.stringify({ environment: state.env, jobName, confirm: typed }),
    });
    toast(r.message, r.success);
  } catch (e) {
    toast(e.message, false);
  } finally {
    state.busy = false;
    setTimeout(refreshStatus, 1200);
  }
}

// ---- history + duration chart --------------------------------------------

async function refreshHistory() {
  const tbody = $('#historyTable tbody');
  dim(tbody, true);

  let panel;
  try {
    panel = await api(`/api/history?env=${encodeURIComponent(state.env)}&top=${state.historyTop}`);
  } catch (e) {
    dim(tbody, false);
    tbody.replaceChildren(errorRow(6, e.message));
    return;
  }
  dim(tbody, false);

  if (panel.error) {
    tbody.replaceChildren(errorRow(6, panel.error));
    $('#durationChart').replaceChildren();
    return;
  }

  const runs = panel.data.slice().reverse();   // oldest left
  drawDurationChart(runs);
  renderHistoryTable(panel.data);

  // Without the status panel, the newest run in the history is what the reconciliation
  // and files tabs should follow - otherwise they open on today, usually empty.
  if (state.access && !state.access.statusPanel && panel.data.length) {
    syncDateToLastRun({ startedLocal: panel.data[0].startedLocal });
  }
}

function errorRow(cols, message) {
  const tr = el('tr');
  const td = el('td');
  td.colSpan = cols;
  td.appendChild(el('div', 'panel-error', message));
  tr.appendChild(td);
  return tr;
}

function renderHistoryTable(runs) {
  const tbody = $('#historyTable tbody');
  tbody.replaceChildren();

  if (!runs.length) {
    const tr = el('tr'); const td = el('td', 'empty', 'No run history.'); td.colSpan = 6;
    tr.appendChild(td); tbody.appendChild(tr);
    return;
  }

  for (const r of runs) {
    const tr = el('tr', 'clickable');
    tr.append(
      tdWith(fmtDateTime(r.startedLocal)),
      tdNode(statusEl(r.outcome)),
      tdWith(fmtDuration(r.durationSeconds), 'num'),
      tdWith(r.failedStepId ? `${r.failedStepId} · ${r.failedStepName || ''}` : '—'),
      tdWith(r.invokedBy || '—'),
      tdWith('view'),
    );
    const detail = el('tr');
    const dcell = el('td'); dcell.colSpan = 6; dcell.style.padding = '0';
    detail.appendChild(dcell);
    detail.hidden = true;

    tr.onclick = async () => {
      if (!detail.hidden) { detail.hidden = true; dcell.replaceChildren(); return; }
      detail.hidden = false;
      dcell.replaceChildren(el('div', 'empty', 'Loading run detail…'));
      try {
        const p = await api(`/api/run/${r.instanceId}?env=${encodeURIComponent(state.env)}`);
        if (p.error) { dcell.replaceChildren(el('div', 'panel-error', p.error)); return; }
        dcell.replaceChildren(buildRunDetail(p.data, r));
      } catch (e) {
        dcell.replaceChildren(el('div', 'panel-error', e.message));
      }
    };

    tbody.append(tr, detail);
  }
}

function buildRunDetail(steps, run) {
  const box = el('div');
  box.style.padding = '10px 0 16px 0';

  if (run.message) {
    const p = el('pre', 'mono', run.message);
    p.style.cssText = 'white-space:pre-wrap;background:var(--wash);padding:10px 12px;border-radius:6px;margin:0 0 12px';
    box.appendChild(p);
  }

  const inner = el('div', 'steps');
  for (const st of steps) {
    const row = el('div', 'step');
    row.append(
      el('div', 'num', String(st.stepId)),
      (() => { const n = el('div', 'name', st.stepName); return n; })(),
      el('div', 'dur', fmtDuration(st.durationSeconds)),
      statusEl(st.outcome),
    );
    inner.appendChild(row);

    if (st.outcome === 'failed' && st.message) {
      const d = el('div', 'step-detail');
      d.appendChild(el('pre', null, st.message));
      inner.appendChild(d);
    }
  }
  box.appendChild(inner);
  return box;
}

function tdWith(text, cls) { return el('td', cls, text); }
function tdNode(node) { const td = el('td'); td.appendChild(node); return td; }

/**
 * One bar per run, duration on a single axis. Colour is the status token because it
 * means good/bad here, not identity - so it ships with a legend and a table twin.
 */
function drawDurationChart(runs) {
  const host = $('#durationChart');
  host.replaceChildren();

  if (!runs.length) {
    host.appendChild(el('div', 'empty', 'No runs in this window.'));
    return;
  }

  // ---- legend. Fixed order, so filtering the window never reshuffles it - colour
  // follows the outcome, never its rank in the current slice.
  const LEGEND_ORDER = ['succeeded', 'failed', 'canceled', 'retry', 'running', 'unknown'];
  const seen = new Set(runs.map((r) => r.outcome));
  const present = LEGEND_ORDER.filter((c) => seen.has(c));
  const legend = el('div', 'legend');
  for (const code of present) {
    const s = STATUS[code] || STATUS.unknown;
    const item = el('span', 'item');
    const sw = el('span', 'swatch');
    sw.style.background = `var(${s.colorVar})`;
    item.append(sw, el('span', null, s.label));
    legend.appendChild(item);
  }
  host.appendChild(legend);

  const W = 1000, H = 250;
  const M = { top: 16, right: 14, bottom: 34, left: 48 };
  const plotW = W - M.left - M.right;
  const plotH = H - M.top - M.bottom;

  const maxSec = Math.max(...runs.map((r) => r.durationSeconds), 60);
  const niceMax = niceCeiling(maxSec);
  const y = (sec) => M.top + plotH - (sec / niceMax) * plotH;

  const band = plotW / runs.length;
  const GAP = 2;                                   // 2px surface gap between adjacent bars
  const barW = Math.max(2, Math.min(34, band - GAP));

  const wrap = el('div', 'chart-wrap');
  const chart = el('div', 'chart');
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label',
    `Run duration for the last ${runs.length} runs. The table below this chart lists every value.`);

  const ns = (tag, attrs = {}) => {
    const n = document.createElementNS('http://www.w3.org/2000/svg', tag);
    for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v);
    return n;
  };

  // ---- gridlines (solid hairlines, one shade off the surface)
  const grid = ns('g', { class: 'grid' });
  const ticks = 4;
  for (let i = 0; i <= ticks; i += 1) {
    const val = (niceMax / ticks) * i;
    const yy = y(val);
    grid.appendChild(ns('line', { x1: M.left, x2: M.left + plotW, y1: yy, y2: yy }));
    const label = ns('text', { x: M.left - 8, y: yy + 3.5, class: 'tick', 'text-anchor': 'end' });
    label.textContent = val >= 3600 ? `${(val / 3600).toFixed(val % 3600 === 0 ? 0 : 1)}h` : `${Math.round(val / 60)}m`;
    grid.appendChild(label);
  }
  svg.appendChild(grid);

  // ---- bars
  const longest = runs.reduce((a, b) => (b.durationSeconds > a.durationSeconds ? b : a), runs[0]);
  const bars = ns('g');

  runs.forEach((r, i) => {
    const cx = M.left + band * i + band / 2;
    const x = cx - barW / 2;
    const yTop = y(r.durationSeconds);
    const h = Math.max(1.5, M.top + plotH - yTop);
    const s = STATUS[r.outcome] || STATUS.unknown;

    const g = ns('g', { class: 'bar', tabindex: '0', role: 'listitem' });

    g.appendChild(ns('path', {
      d: roundedTopBar(x, yTop, barW, h, 4),       // 4px rounded data-end, anchored to the baseline
      fill: cssVar(s.colorVar),
    }));

    // Hit area spans the full band and plot height so the target clears ~24px.
    g.appendChild(ns('rect', {
      class: 'hit',
      x: cx - Math.max(band, 24) / 2, y: M.top,
      width: Math.max(band, 24), height: plotH,
    }));

    const show = (ev) => showTip(ev, r);
    g.addEventListener('mouseenter', show);
    g.addEventListener('mousemove', show);
    g.addEventListener('focus', show);
    g.addEventListener('mouseleave', hideTip);
    g.addEventListener('blur', hideTip);

    bars.appendChild(g);
  });
  svg.appendChild(bars);

  // ---- direct label on the extreme only (never a number on every bar)
  {
    const i = runs.indexOf(longest);
    const cx = M.left + band * i + band / 2;
    const t = ns('text', {
      x: Math.min(Math.max(cx, M.left + 18), M.left + plotW - 18),
      y: y(longest.durationSeconds) - 7,
      class: 'direct-label', 'text-anchor': 'middle',
    });
    t.textContent = fmtDuration(longest.durationSeconds);
    svg.appendChild(t);
  }

  // ---- axes
  const axis = ns('g', { class: 'axis' });
  axis.appendChild(ns('line', { x1: M.left, x2: M.left + plotW, y1: M.top + plotH, y2: M.top + plotH }));
  svg.appendChild(axis);

  // x labels: first, middle, last only - more would collide
  for (const i of [...new Set([0, Math.floor(runs.length / 2), runs.length - 1])]) {
    const t = ns('text', {
      x: M.left + band * i + band / 2,
      y: M.top + plotH + 18,
      class: 'tick',
      'text-anchor': i === 0 ? 'start' : i === runs.length - 1 ? 'end' : 'middle',
    });
    t.textContent = new Date(runs[i].startedLocal).toLocaleDateString([], { month: 'short', day: 'numeric' });
    svg.appendChild(t);
  }

  chart.appendChild(svg);
  wrap.appendChild(chart);

  const tip = el('div', 'tooltip');
  wrap.appendChild(tip);
  host.appendChild(wrap);

  function showTip(ev, r) {
    tip.replaceChildren();
    tip.appendChild(el('div', 't-title', fmtDateTime(r.startedLocal)));

    const addRow = (k, v) => {
      const row = el('div', 't-row');
      row.append(el('span', null, k), el('span', 'tv', v));
      tip.appendChild(row);
    };
    addRow('Outcome', (STATUS[r.outcome] || STATUS.unknown).label);
    addRow('Duration', fmtDuration(r.durationSeconds));
    if (r.failedStepId) addRow('Failed step', String(r.failedStepId));
    if (r.invokedBy) addRow('Invoked by', r.invokedBy);

    const box = wrap.getBoundingClientRect();
    const px = (ev.clientX ?? box.left + box.width / 2) - box.left;
    const py = (ev.clientY ?? box.top + 40) - box.top;
    tip.style.left = `${Math.min(Math.max(px + 14, 4), box.width - 190)}px`;
    tip.style.top = `${Math.max(py - 10, 4)}px`;
    tip.classList.add('on');
  }
  function hideTip() { tip.classList.remove('on'); }
}

function roundedTopBar(x, y, w, h, r) {
  const rr = Math.min(r, w / 2, h);
  return `M${x},${y + h} L${x},${y + rr} Q${x},${y} ${x + rr},${y} `
       + `L${x + w - rr},${y} Q${x + w},${y} ${x + w},${y + rr} L${x + w},${y + h} Z`;
}

function niceCeiling(seconds) {
  const steps = [300, 900, 1800, 3600, 7200, 10800, 14400, 21600, 28800, 43200, 64800, 86400];
  return steps.find((s) => s >= seconds) || Math.ceil(seconds / 86400) * 86400;
}

// ---- recon ----------------------------------------------------------------

async function refreshRecon() {
  const tbody = $('#reconTable tbody');
  dim(tbody, true);
  $('#reconMode').textContent =
    (state.reconExact ? 'exact row counts' : 'metadata row counts - can lag a load that just finished')
    + (state.reconRecords ? ' · records read from the files' : ' · file LINE counts');
  $('#recordsBtn').disabled = state.reconRecords;
  $('#exactBtn').disabled = state.reconExact;

  let panel;
  try {
    panel = await api(
      `/api/recon?env=${encodeURIComponent(state.env)}&date=${state.date}`
      + `&exact=${state.reconExact}&records=${state.reconRecords}`);
  } catch (e) {
    dim(tbody, false); tbody.replaceChildren(errorRow(7, e.message)); return;
  }
  dim(tbody, false);

  if (panel.error) { tbody.replaceChildren(errorRow(7, panel.error)); return; }
  if (!panel.data.length) {
    tbody.replaceChildren(errorRow(7, 'No recon tables are configured for this environment.'));
    return;
  }

  // The recon tables hold only the most recent load, so they can only be compared with
  // the most recent run's files. Pick an older date and the comparison is between that
  // day's file counts and today's tables - arithmetic that means nothing.
  const staleWarn = $('#reconStale');
  if (state.lastRunDate && state.date !== state.lastRunDate) {
    staleWarn.replaceChildren(el('div', 'banner err',
      `Showing ${state.date}, but the ODS recon tables hold only the latest load `
      + `(${state.lastRunDate}). Table rows and Difference are not meaningful for an earlier `
      + 'date - switch the extract date back to compare like with like.'));
  } else {
    staleWarn.replaceChildren();
  }

  tbody.replaceChildren();
  for (const r of panel.data) {
    const tr = el('tr');

    const label = {
      succeeded: 'Match',
      failed: r.difference > 0 ? 'Short' : 'Over',
      // Once records have been counted the rows DO reconcile, so say so - while keeping
      // the row amber, because the file itself is still malformed.
      artifact: r.continuationLines > 0 ? 'Rows OK, file split' : 'Extra lines',
      empty: 'No rows at all',
    }[r.status] || 'No data';

    const diffCell = tdWith(
      r.difference === null || r.difference === undefined
        ? '—'
        : (r.difference > 0 ? `+${fmtNumber(r.difference)}` : fmtNumber(r.difference)),
      'num');

    const recordsCell = tdWith(
      r.fileRecords === null || r.fileRecords === undefined ? '—' : fmtNumber(r.fileRecords),
      'num');
    if (r.continuationLines > 0) {
      recordsCell.appendChild(el('span', 'flag warn', `+${fmtNumber(r.continuationLines)} split`));
      recordsCell.title = `${fmtNumber(r.continuationLines)} physical line(s) continue a previous `
                        + 'record instead of starting a new one, because a field value contains '
                        + 'an embedded CR/LF.';
    }

    const statusCell = tdNode(statusEl(r.status, label));
    if (r.status === 'empty') {
      statusCell.title = 'The table has no rows and the file has no records. They agree, which is '
                       + 'why this is NOT reported as a match: an extract that produced nothing '
                       + 'reconciles perfectly against a file containing nothing. Check whether '
                       + 'the source returned any data - the job will have reported success either way.';
    }
    if (r.status === 'artifact') {
      statusCell.title = r.continuationLines > 0
        // Records reconcile but the file is still wrong - say both, do not imply all clear.
        ? `Every row reached the file (${fmtNumber(r.fileRecords)} records = ${fmtNumber(r.tableRows)} rows), `
          + `but ${fmtNumber(r.continuationLines)} record(s) are split across lines by an embedded `
          + 'CR/LF. No data is lost, but two things are still wrong: the file is malformed for '
          + 'anything that reads it one line at a time, and the count file sent to Strata declares '
          + `${fmtNumber(r.fileLines)} where the file holds ${fmtNumber(r.fileRecords)}. `
          + 'Cleaning the source columns fixes both.'
        : 'The file has more LINES than the table has rows, which cannot happen by losing data. '
          + 'A field value contains an embedded line break - which also means the count declared '
          + 'to Strata is overstated. Use "Count records in files" to measure it exactly.';
    }

    // A file over 1 GB arrives as numbered parts; every count here is their sum. Say so,
    // and call out a part that is missing - those records are missing too.
    const nameCell = tdWith(r.key);
    if (r.fileName) nameCell.title = r.fileName;
    if (r.fileParts > 1) {
      const f = el('span', 'flag', `${r.fileParts} parts`);
      f.title = `Split to stay under 1 GB: ${r.fileName}. Counts are the sum of all parts.`;
      nameCell.appendChild(f);
    }
    if (r.fileNote) {
      const f = el('span', 'flag warn', 'check parts');
      f.title = r.fileNote;
      nameCell.appendChild(f);
    }

    tr.append(
      nameCell,
      statusCell,
      tdWith(fmtNumber(r.tableRows), 'num'),
      tdWith(fmtNumber(r.fileLines), 'num'),
      recordsCell,
      diffCell,
      tdWith(r.fileModifiedLocal ? fmtDateTime(r.fileModifiedLocal) : 'not found'),
    );
    tbody.appendChild(tr);
  }
}

// ---- files ----------------------------------------------------------------

async function refreshFiles() {
  const tbody = $('#filesTable tbody');
  const summary = $('#filesSummary');
  dim(tbody, true);

  let panel;
  try {
    panel = await api(`/api/files?env=${encodeURIComponent(state.env)}&date=${state.date}`);
  } catch (e) {
    dim(tbody, false); summary.replaceChildren(); tbody.replaceChildren(errorRow(4, e.message)); return;
  }
  dim(tbody, false);

  if (panel.error) { summary.replaceChildren(); tbody.replaceChildren(errorRow(4, panel.error)); return; }

  const files = panel.data;
  const out = files.filter((f) => f.direction === 'OUT').length;
  const inb = files.filter((f) => f.direction === 'IN').length;

  const line = el('div', 'hero-sub');
  line.textContent = files.length
    ? `${fmtDateTime(state.date + 'T00:00:00').split(',')[0]} · ${files.length} transferred `
      + `· ${out} sent to Strata · ${inb} received from Strata`
    : '';
  summary.replaceChildren(line);

  tbody.replaceChildren();
  if (!files.length) {
    tbody.replaceChildren(errorRow(4,
      `Nothing was transferred on ${state.date}. The archive is reachable; pick another `
      + 'extract date, or check whether the pickup ran.'));
    return;
  }

  for (const f of files) {
    const tr = el('tr');

    const dir = el('td');
    if (f.direction) {
      const chip = el('span', `flag${f.direction === 'IN' ? ' warn' : ''}`, f.direction);
      chip.title = f.direction === 'IN'
        ? 'Received from Strata - usually a load report.'
        : 'Sent to Strata.';
      dir.appendChild(chip);
    } else {
      dir.appendChild(el('span', 'sub', '—'));
    }

    const name = el('td');
    name.appendChild(el('div', 'mono', f.originalName));
    // The CLEO suffix matters when chasing a specific transfer, but it is noise by default.
    if (f.name !== f.originalName) {
      const raw = el('div', 'sub mono');
      raw.textContent = f.name;
      raw.style.display = 'none';
      name.appendChild(raw);
      name.style.cursor = 'pointer';
      name.onclick = () => { raw.style.display = raw.style.display === 'none' ? '' : 'none'; };
      name.title = 'Click to show the archived file name';
    }

    tr.append(
      dir,
      name,
      tdWith(fmtBytes(f.sizeBytes), 'num'),
      tdWith(fmtDateTime(f.transferredLocal || f.modifiedLocal)),
    );
    tbody.appendChild(tr);
  }
}

// ---- ssis -----------------------------------------------------------------

async function refreshSsis() {
  const tbody = $('#ssisTable tbody');
  const summary = $('#ssisSummary');
  dim(tbody, true);
  $('#ssisMessages').replaceChildren();

  let panel;
  try {
    panel = await api(`/api/ssis?env=${encodeURIComponent(state.env)}`);
  } catch (e) {
    dim(tbody, false); summary.replaceChildren(); tbody.replaceChildren(errorRow(6, e.message)); return;
  }
  dim(tbody, false);

  if (panel.error) {
    summary.replaceChildren();
    tbody.replaceChildren(errorRow(6, panel.error));
    return;
  }

  const rep = panel.data;

  // ---- summary line
  summary.replaceChildren();
  if (!rep.window) {
    summary.appendChild(el('div', 'empty', 'No run to report on yet.'));
  } else {
    const bits = [
      `${rep.total} package${rep.total === 1 ? '' : 's'}`,
      `${rep.succeeded} succeeded`,
    ];
    if (rep.failed) bits.push(`${rep.failed} failed`);
    if (rep.running) bits.push(`${rep.running} still running`);
    if (rep.notRun) bits.push(`${rep.notRun} did not run`);
    // "did not run" is expected after a partial restart, so do not let it read as a fault.
    const caveat = rep.notRun
      ? ' — a package shows as "did not run" when its step was not part of this pass.'
      : '';

    const line = el('div', 'hero-sub');
    line.textContent = `Run of ${fmtDateTime(rep.window.from)}`
      + `${rep.window.isRunning ? ' (in progress)' : ''} · ` + bits.join(' · ') + caveat;
    summary.appendChild(line);
  }

  if (!rep.packages.length) {
    tbody.replaceChildren(errorRow(6,
      'No SSIS steps were found on this job or its child jobs. If that is wrong, check that '
      + 'the child jobs are listed under ChildJobs in appsettings.json.'));
    return;
  }

  tbody.replaceChildren();
  let lastJob = null;
  for (const p of rep.packages) {
    const tr = el('tr');
    const clickable = p.executionId !== null && p.executionId !== undefined;
    if (clickable) tr.classList.add('clickable');

    // Only name the launching job on its first row, so the grouping is readable.
    const jobCell = p.jobName === lastJob ? '' : p.jobName;
    lastJob = p.jobName;

    const known = Object.prototype.hasOwnProperty.call(STATUS, p.status);

    tr.append(
      tdWith(jobCell),
      tdWith(p.package, 'mono'),
      tdNode(statusEl(known ? p.status : 'unknown', known ? undefined : p.status)),
      tdWith(p.startLocal ? fmtDateTime(p.startLocal) : '—'),
      tdWith(p.durationSeconds !== null && p.durationSeconds !== undefined
        ? fmtDuration(p.durationSeconds) : '—', 'num'),
      tdWith(p.project),
    );

    if (p.attempts > 1) {
      tr.lastChild.appendChild(el('span', 'flag', `${p.attempts} attempts`));
    }
    if (clickable) tr.onclick = () => loadSsisMessages(p.executionId);
    tbody.appendChild(tr);
  }
}

async function loadSsisMessages(executionId) {
  const host = $('#ssisMessages');
  host.replaceChildren(el('div', 'empty', `Loading messages for execution ${executionId}…`));

  try {
    const panel = await api(`/api/ssis/${executionId}/messages?env=${encodeURIComponent(state.env)}`);
    if (panel.error) { panelError(host, panel.error); return; }
    if (!panel.data.length) {
      host.replaceChildren(el('div', 'empty', `Execution ${executionId} logged no errors or warnings.`));
      return;
    }

    const box = el('div');
    box.style.marginTop = '16px';
    box.appendChild(el('h2', null, `Messages · execution ${executionId}`));
    const scroll = el('div', 'scroll');
    const t = el('table');
    const thead = el('thead');
    thead.innerHTML = '<tr><th>Time</th><th>Type</th><th>Source</th><th>Message</th></tr>';
    const tb = el('tbody');
    for (const m of panel.data) {
      const tr = el('tr');
      tr.append(
        tdWith(fmtDateTime(m.timeLocal)),
        tdWith(m.messageType),
        tdWith(m.sourceName),
        tdWith(m.message),
      );
      tb.appendChild(tr);
    }
    t.append(thead, tb);
    scroll.appendChild(t);
    box.appendChild(scroll);
    host.replaceChildren(box);
  } catch (e) {
    panelError(host, e.message);
  }
}

// ---- recon_current upserts ------------------------------------------------

/** Remembers the chosen day across refreshes; null means "whatever is newest". */
let upsertDay = null;

async function refreshUpserts() {
  const tbody = $('#upsertTable tbody');
  dim(tbody, true);

  let panel;
  try {
    const q = upsertDay ? `&date=${encodeURIComponent(upsertDay)}` : '';
    panel = await api(`/api/upserts?env=${encodeURIComponent(state.env)}${q}`);
  } catch (e) {
    dim(tbody, false);
    tbody.replaceChildren(errorRow(7, e.message));
    return;
  }
  dim(tbody, false);

  if (panel.error) { tbody.replaceChildren(errorRow(7, panel.error)); return; }

  const d = panel.data;
  renderUpsertFailures(d);
  renderUpsertSummary(d);
  renderUpsertDatePicker(d);
  renderUpsertRows(d);
  renderUpsertTrends(d);
}

/**
 * Failures for the SELECTED day only. A day that went cleanly shows nothing here, even
 * where an earlier day failed - the panel describes the day you picked. Which other
 * days went wrong is carried by the day picker, which marks them.
 */
function renderUpsertFailures(d) {
  const host = $('#upsertFailures');
  if (!d.failures.length) { host.replaceChildren(); return; }

  const box = el('div', 'banner err stack');
  const head = el('div');
  head.append(el('strong', null,
    `${d.failures.length} failed upsert${d.failures.length === 1 ? '' : 's'} on `
    + isoDay(d.processDate)));
  box.appendChild(head);

  for (const f of d.failures) {
    const line = el('div', 'sub');
    line.style.marginTop = '6px';
    line.append(
      el('strong', null, f.datasetKey || f.processName),
      document.createTextNode(' — '),
      el('span', null, (f.errorMessage || 'no error text recorded').split('\n')[0]));
    box.appendChild(line);
  }
  host.replaceChildren(box);
}

function renderUpsertSummary(d) {
  const host = $('#upsertSummary');
  if (!d.processDate) {
    host.replaceChildren(el('div', 'empty',
      'This log has no rows yet. The Upsert Process for Recon_Current Tables step writes '
      + 'one row per dataset each time it runs.'));
    return;
  }

  // Nothing on this day: the table says so with the detail. A summary line of all
  // zeroes underneath it would just look like a second, different problem.
  if (!d.rows.length) { host.replaceChildren(); return; }

  const rows = d.rows;
  const updated = rows.reduce((a, r) => a + r.rowsUpdated, 0);
  const inserted = rows.reduce((a, r) => a + r.rowsInserted, 0);
  const total = rows.reduce((a, r) => a + r.totalRowsProcessed, 0);
  const idle = rows.filter((r) => r.totalRowsProcessed === 0);

  const bits = [];
  bits.push(`${rows.length} dataset${rows.length === 1 ? '' : 's'}`);
  bits.push(`${fmtNumber(total)} rows processed`);
  bits.push(`${fmtNumber(updated)} updated · ${fmtNumber(inserted)} inserted`);

  const box = el('div', 'upsert-note');
  box.textContent = bits.join(' · ');

  if (d.retriedToday.length) {
    box.appendChild(el('div', 'flag warn',
      `Logged more than once this day: ${d.retriedToday.join(', ')} — the step was retried.`));
  }
  if (idle.length) {
    // Surgical is permanently 0 on DEV because its extract step is commented out.
    // Said plainly so it is not read as a daily failure.
    box.appendChild(el('div', 'sub',
      `Processed no rows: ${idle.map((r) => r.datasetKey || r.processName).join(', ')}. `
      + 'That is expected where the dataset’s extract step is switched off; '
      + 'it is worth a look where it is not.'));
  }
  host.replaceChildren(box);
}

function renderUpsertDatePicker(d) {
  const sel = $('#upsertDate');
  sel.replaceChildren();

  // A day that failed is labelled as such in the list. The failures themselves stay
  // scoped to the day you are viewing, so this is what stops an earlier bad day from
  // becoming invisible the moment you look at a good one.
  const shown = isoDay(d.processDate);
  const haveShown = d.availableDays.some((x) => isoDay(x.date) === shown);

  // A date the log has no rows for should still appear as the selection, or the picker
  // says one day while the table reports another.
  if (shown && !haveShown) {
    const o = el('option', null, `${shown}  —  no rows`);
    o.value = shown;
    o.selected = true;
    sel.appendChild(o);
  }

  for (const day of d.availableDays) {
    const iso = isoDay(day.date);
    const o = el('option', null,
      day.failureCount > 0
        ? `${iso}  —  ${day.failureCount} failed`
        : iso);
    o.value = iso;
    if (iso === shown) o.selected = true;
    sel.appendChild(o);
  }
  sel.disabled = d.availableDays.length === 0;
  sel.onchange = () => { upsertDay = sel.value; refreshUpserts(); };

  const badDays = d.availableDays.filter((x) => x.failureCount > 0).length;
  const parts = [];
  if (d.distinctDays) {
    parts.push(`${d.distinctDays} day${d.distinctDays === 1 ? '' : 's'} recorded`);
    parts.push(`${fmtNumber(d.totalRows)} rows in the log`);
    if (badDays) parts.push(`${badDays} with failures`);
  }
  $('#upsertSpan').textContent = parts.join(' · ');
}

function renderUpsertRows(d) {
  const tbody = $('#upsertTable tbody');
  tbody.replaceChildren();

  if (!d.rows.length) {
    // Name the day and what the log does hold. "No rows for this day" on its own reads
    // like the step never ran, when the usual cause is simply a day this environment
    // has no record of.
    const days = d.availableDays.map((x) => isoDay(x.date));
    let msg = `No upsert rows recorded on ${isoDay(d.processDate)}.`;
    if (days.length) {
      msg += ` ${state.env} has data for `
           + (days.length > 6 ? `${days.length} other days, most recently ${days[0]}.`
                              : `${days.join(', ')}.`);
    } else {
      msg += ' This environment has no upsert log rows at all yet.';
    }
    tbody.replaceChildren(errorRow(7, msg));
    return;
  }

  for (const r of d.rows) {
    const tr = el('tr');
    tr.append(
      tdWith(r.datasetKey || r.processName),
      tdNode(statusEl(r.outcome, r.outcome === 'succeeded' ? 'Success' : r.rawStatus)),
      tdWith(fmtNumber(r.rowsUpdated), 'num'),
      tdWith(fmtNumber(r.rowsInserted), 'num'),
      tdWith(fmtNumber(r.totalRowsProcessed), 'num'),
      tdNode(splitBar(r)),
      tdWith(fmtDateTime(r.finishedLocal)));

    if (r.errorMessage) {
      tr.title = r.errorMessage;
      tr.classList.add('has-error');
    }
    tbody.appendChild(tr);
  }
}

/** Proportion as a bar AND as text - never colour alone. */
function splitBar(r) {
  const wrap = el('div', 'split');
  const total = r.rowsUpdated + r.rowsInserted;

  if (total <= 0) {
    wrap.appendChild(el('span', 'pct', '—'));
    return wrap;
  }

  const pctUpd = Math.round((r.rowsUpdated / total) * 100);
  const track = el('div', 'track');
  const u = el('div', 'seg updated');
  u.style.width = `${pctUpd}%`;
  const i = el('div', 'seg inserted');
  i.style.width = `${100 - pctUpd}%`;
  track.append(u, i);

  wrap.append(track, el('span', 'pct', `${pctUpd}% upd`));
  wrap.title = `${fmtNumber(r.rowsUpdated)} updated, ${fmtNumber(r.rowsInserted)} inserted`;
  return wrap;
}

/**
 * Small multiples: one stacked bar per day per dataset, newest on the right. Each
 * dataset gets its own vertical scale - volumes differ by two orders of magnitude,
 * so a shared axis would flatten every small dataset to nothing.
 */
function renderUpsertTrends(d) {
  const host = $('#upsertTrends');
  host.replaceChildren();

  if (!d.trends.length) {
    host.appendChild(el('div', 'empty', 'Nothing recorded yet.'));
    return;
  }

  const legend = el('div', 'legend');
  for (const [cls, label] of [['updated', 'Updated'], ['inserted', 'Inserted']]) {
    const key = el('span', 'key');
    key.append(el('span', `swatch ${cls}`), el('span', null, label));
    legend.appendChild(key);
  }
  legend.appendChild(el('span', 'sub', 'Each dataset is on its own scale.'));
  host.appendChild(legend);

  const grid = el('div', 'trend-grid');
  for (const t of d.trends) {
    grid.appendChild(trendCard(t));
  }
  host.appendChild(grid);
}

function trendCard(t) {
  const card = el('div', 'trend');
  card.appendChild(el('h3', null, t.datasetKey || t.processName));

  // Oldest left, newest right - the service returns newest first.
  const points = [...t.points].reverse();
  const max = Math.max(1, ...points.map((p) => p.totalRowsProcessed));
  const latest = points[points.length - 1];

  card.appendChild(el('div', 'meta',
    `latest ${fmtNumber(latest.totalRowsProcessed)} rows · ${points.length} day`
    + `${points.length === 1 ? '' : 's'}`));

  const W = 240, H = 76, PAD_B = 14, GAP = 2;
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
  svg.setAttribute('preserveAspectRatio', 'none');
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label',
    `${t.datasetKey || t.processName}: updated and inserted rows per day, `
    + `${points.length} days, latest ${latest.totalRowsProcessed} rows.`);

  const plotH = H - PAD_B;
  const slot = W / points.length;
  const barW = Math.max(6, Math.min(26, slot - 6));

  // Baseline, recessive.
  const base = svgEl('line', {
    x1: 0, y1: plotH, x2: W, y2: plotH,
    stroke: cssVar('--baseline'), 'stroke-width': 1,
  });
  svg.appendChild(base);

  points.forEach((p, idx) => {
    const cx = slot * idx + slot / 2;
    const x = cx - barW / 2;

    if (p.totalRowsProcessed === 0) {
      // A failed or no-op day has no height at all. Draw a mark so the day is still
      // visible - an invisible bar reads as "no data", which is a different thing.
      const glyph = svgEl('text', {
        x: cx, y: plotH - 3, 'text-anchor': 'middle',
        class: p.outcome === 'failed' ? 'failmark' : 'daylabel',
      });
      glyph.textContent = p.outcome === 'failed' ? '✕' : '·';
      svg.appendChild(glyph);
    } else {
      const totalH = Math.max(2, (p.totalRowsProcessed / max) * (plotH - 4));
      const updH = (p.rowsUpdated / p.totalRowsProcessed) * totalH;
      const insH = Math.max(0, totalH - updH - GAP);

      // Inserted sits on top with a 2px surface gap between the two fills.
      const yUpd = plotH - updH;
      svg.appendChild(svgEl('rect', {
        x, y: yUpd, width: barW, height: Math.max(0, updH),
        fill: cssVar('--series-1'),
      }));
      if (insH > 0) {
        svg.appendChild(svgEl('rect', {
          x, y: yUpd - GAP - insH, width: barW, height: insH,
          fill: cssVar('--series-2'), rx: 3,
        }));
      }
    }

    const hit = svgEl('rect', {
      x: slot * idx, y: 0, width: slot, height: plotH, class: 'bar-hit',
    });
    const title = document.createElementNS('http://www.w3.org/2000/svg', 'title');
    title.textContent =
      `${isoDay(p.processDate)}\n`
      + `${fmtNumber(p.rowsUpdated)} updated\n`
      + `${fmtNumber(p.rowsInserted)} inserted\n`
      + `${fmtNumber(p.totalRowsProcessed)} total · ${p.outcome}`;
    hit.appendChild(title);
    svg.appendChild(hit);
  });

  // Label the ends only. A label under every bar collides at this width.
  const first = svgEl('text', { x: 0, y: H - 2, class: 'daylabel', 'text-anchor': 'start' });
  first.textContent = shortDay(points[0].processDate);
  svg.appendChild(first);

  if (points.length > 1) {
    const last = svgEl('text', { x: W, y: H - 2, class: 'daylabel', 'text-anchor': 'end' });
    last.textContent = shortDay(latest.processDate);
    svg.appendChild(last);
  }

  card.appendChild(svg);
  return card;
}

function svgEl(tag, attrs) {
  const n = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, String(v));
  return n;
}

/** The API returns dates as local midnight; take the date part without shifting zone. */
function isoDay(value) {
  if (!value) return '';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return String(value).slice(0, 10);
  return todayIso(d);
}

function shortDay(value) {
  const iso = isoDay(value);
  return iso ? iso.slice(5) : '';
}

// ---- extract volume -------------------------------------------------------

/** preset: '30' | '90' | '180' | 'all' | null (custom). data: the last report. */
const vol = { preset: '90', from: null, to: null, data: null, showTable: false };

const SVGNS = 'http://www.w3.org/2000/svg';
function svgNode(tag, attrs, cls) {
  const n = document.createElementNS(SVGNS, tag);
  for (const [k, v] of Object.entries(attrs || {})) n.setAttribute(k, String(v));
  if (cls) n.setAttribute('class', cls);
  return n;
}

/** 1,234 -> 1.2K; 17,955,188 -> 18M. Axis and end labels only - tables keep full numbers. */
function fmtCompact(n) {
  if (n === null || n === undefined) return '—';
  const a = Math.abs(n);
  const f = (v, s) => `${Number.isInteger(v) || v >= 100 ? Math.round(v) : Math.round(v * 10) / 10}${s}`;
  if (a >= 1e9) return f(n / 1e9, 'B');
  if (a >= 1e6) return f(n / 1e6, 'M');
  if (a >= 1e3) return f(n / 1e3, 'K');
  return String(n);
}

function niceMax(v) {
  if (v <= 0) return 1;
  const p = 10 ** Math.floor(Math.log10(v));
  const m = v / p;
  return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10) * p;
}

function dayLabel(iso, withWeekday) {
  const d = new Date(`${isoDay(iso)}T00:00:00`);
  return d.toLocaleDateString([], withWeekday
    ? { weekday: 'short', month: 'short', day: 'numeric' }
    : { month: 'short', day: 'numeric' });
}

function wireVolume() {
  for (const b of document.querySelectorAll('#volPresets button')) {
    b.onclick = () => { vol.preset = b.dataset.days; vol.from = vol.to = null; refreshVolume(); };
  }
  $('#volApply').onclick = () => {
    const f = $('#volFrom').value, t = $('#volTo').value;
    if (!f || !t) { toast('Choose both a From and a To date.', false); return; }
    vol.preset = null; vol.from = f; vol.to = t;
    refreshVolume();
  };
  $('#volTableBtn').onclick = () => {
    vol.showTable = !vol.showTable;
    $('#volTableWrap').hidden = !vol.showTable;
    $('#volTableBtn').textContent = vol.showTable ? 'Hide table' : 'Show as table';
    $('#volTableBtn').setAttribute('aria-expanded', String(vol.showTable));
    if (vol.showTable && vol.data) renderVolumeTable(vol.data);
  };
  // Charts are drawn to the container's real width so their text is never stretched.
  let t = null;
  window.addEventListener('resize', () => {
    clearTimeout(t);
    t = setTimeout(() => { if (state.tab === 'volume' && vol.data) renderVolumeCharts(vol.data); }, 150);
  });
}

function volumeQuery() {
  if (vol.preset === 'all') return 'from=2000-01-01';
  if (vol.preset) {
    const to = new Date();
    const from = new Date(); from.setDate(to.getDate() - (Number(vol.preset) - 1));
    return `from=${todayIso(from)}&to=${todayIso(to)}`;
  }
  return `from=${vol.from}&to=${vol.to}`;
}

async function refreshVolume() {
  for (const b of document.querySelectorAll('#volPresets button')) {
    b.setAttribute('aria-pressed', String(b.dataset.days === vol.preset));
  }
  const body = $('#volBody');
  dim(body, true);   // hold the previous render, no skeleton flash

  let panel;
  try {
    panel = await api(`/api/volumes?env=${encodeURIComponent(state.env)}&${volumeQuery()}`);
  } catch (e) { dim(body, false); panelError($('#volZeroBanner'), e.message); return; }
  dim(body, false);
  if (panel.error) { panelError($('#volZeroBanner'), panel.error); return; }

  const d = panel.data;
  vol.data = d;
  $('#volFrom').value = isoDay(d.from);
  $('#volTo').value = isoDay(d.to);
  const days = Math.round((new Date(isoDay(d.to)) - new Date(isoDay(d.from))) / 864e5) + 1;
  $('#volSpan').textContent = `${dayLabel(d.from)} – ${dayLabel(d.to)} · ${days} days`
    + (d.earliestInLog ? ` · log starts ${dayLabel(d.earliestInLog)}` : '');

  renderVolumeZeroBanner(d);
  renderVolumeSummary(d);
  renderVolumeCharts(d);
  if (vol.showTable) renderVolumeTable(d);
}

/**
 * The silent empty run, said in words above everything. On PROD every one of these has
 * been a Thursday since 2026-05-28 - so the weekday is named when they share one.
 */
function renderVolumeZeroBanner(d) {
  const host = $('#volZeroBanner');
  const z = d.allZeroDays;
  if (!z.length) { host.replaceChildren(); return; }

  const weekdays = [...new Set(z.map((x) => new Date(`${isoDay(x)}T00:00:00`)
    .toLocaleDateString([], { weekday: 'long' })))];
  const box = el('div', 'banner err stack');
  const head = el('div');
  head.append(el('strong', null, `✕ ${z.length} day${z.length === 1 ? '' : 's'} in this range the extract succeeded and pulled no rows at all`));
  box.appendChild(head);
  box.appendChild(el('div', 'sub',
    (weekdays.length === 1 && z.length > 1 ? `Every one a ${weekdays[0]}. ` : '')
    + 'Every staging package reported Succeeded with 0 rows, so the job went green and '
    + 'header-only files went to Strata. Dates: '
    + z.map((x) => dayLabel(x, true)).join(', ') + '.'));
  host.replaceChildren(box);
}

function renderVolumeSummary(d) {
  const tbody = $('#volSummary tbody');
  tbody.replaceChildren();

  for (const s of d.series) {
    const m = s.summary;
    const tr = el('tr');

    const name = tdWith(s.label);
    if (s.packages.length > 1) name.title = `Packages in range: ${s.packages.join(', ')}`;

    const latest = tdWith(m.latest === null ? '—' : fmtNumber(m.latest), 'num');
    if (m.latest === 0) latest.appendChild(el('span', 'vol-zero-key', ' ✕'));
    if (m.latestDate) latest.title = dayLabel(m.latestDate, true);

    const peak = tdWith(m.peak === null ? '—' : `${fmtNumber(m.peak)}  (${dayLabel(m.peakDate)})`, 'num');

    const change = tdWith('—', 'num');
    if (m.last7Average !== null && m.prior7Average) {
      const pct = Math.round(((m.last7Average - m.prior7Average) / m.prior7Average) * 100);
      change.textContent = `${pct > 0 ? '▲' : pct < 0 ? '▼' : ''} ${pct > 0 ? '+' : ''}${fmtNumber(pct)}%`;
      change.title = `${fmtNumber(m.last7Average)} a day over the last 7 loading days, `
                   + `against ${fmtNumber(m.prior7Average)} over the 7 before.`;
      // The early warning: a sustained rise, flagged in words as well as the arrow.
      if (pct >= 50) change.appendChild(el('span', 'flag warn', 'influx'));
      else if (pct <= -50) change.appendChild(el('span', 'flag warn', 'drop'));
    }

    const zero = tdWith(fmtNumber(m.zeroDays), 'num');
    if (m.zeroDays > 0) zero.appendChild(el('span', 'vol-zero-key', ' ✕'));

    tr.append(name, latest, tdWith(m.typical === null ? '—' : fmtNumber(m.typical), 'num'), peak, change, zero);
    tbody.appendChild(tr);
  }
}

function renderVolumeCharts(d) {
  lineChart($('#volTotal'), d.total, { height: 190, label: 'All ten datasets' });

  const grid = $('#volGrid');
  grid.replaceChildren();
  for (const s of d.series) {
    const card = el('div', 'vol-card');
    const h = el('h4', null, s.label);
    card.appendChild(h);
    const m = s.summary;
    card.appendChild(el('div', 'meta',
      `typical ${fmtCompact(m.typical)} · peak ${fmtCompact(m.peak)}`
      + (m.zeroDays ? ` · ${m.zeroDays} zero-row day${m.zeroDays === 1 ? '' : 's'}` : '')));
    const host = el('div', 'vol-chart');
    card.appendChild(host);
    grid.appendChild(card);
    lineChart(host, s.points, { height: 104, label: s.label });
  }
}

/**
 * One series over time. The line joins days that loaded data; a day that succeeded
 * with 0 rows is a ✕ on the baseline instead of dragging the line down - that keeps the
 * trend readable while the empty run stays impossible to miss. No successful run is a gap.
 */
function lineChart(host, points, { height, label }) {
  host.replaceChildren();
  const W = Math.max(240, Math.floor(host.clientWidth || 600));
  const padL = 44, padR = 54, padT = 10, axisH = 18;
  const H = height + axisH;
  const plotW = W - padL - padR, plotH = height - padT;

  const values = points.map((p) => p.rows).filter((v) => v !== null);
  const max = niceMax(Math.max(0, ...values));
  const n = points.length;
  const x = (i) => padL + (n <= 1 ? plotW / 2 : (i / (n - 1)) * plotW);
  const y = (v) => padT + plotH - (v / max) * plotH;

  const svg = svgNode('svg', { viewBox: `0 0 ${W} ${H}`, width: W, height: H, role: 'img',
    'aria-label': `${label}: rows extracted per day, ${points.length} days, peak ${fmtNumber(Math.max(0, ...values))}.` });

  // Recessive hairline grid: baseline, middle, top - labelled compactly.
  for (const [v, cls] of [[0, 'vol-baseline'], [max / 2, 'vol-gridline'], [max, 'vol-gridline']]) {
    svg.appendChild(svgNode('line', { x1: padL, x2: W - padR, y1: y(v), y2: y(v) }, cls));
    const t = svgNode('text', { x: padL - 6, y: y(v) + 3, 'text-anchor': 'end' }, 'vol-axis');
    t.textContent = fmtCompact(v);
    svg.appendChild(t);
  }

  // Line segments over days with data; zero and missing days break the line.
  let d = '';
  let drawing = false;
  points.forEach((p, i) => {
    if (p.rows !== null && p.rows > 0) {
      d += `${drawing ? 'L' : 'M'}${x(i).toFixed(1)},${y(p.rows).toFixed(1)}`;
      drawing = true;
    } else {
      drawing = false;
    }
  });
  if (d) svg.appendChild(svgNode('path', { d }, 'vol-line'));

  // A loading day with no loading neighbour has no segment to sit on, so the line alone
  // would draw nothing for it. Common on DEV, which runs ad hoc. Give it a dot.
  const loads = (i) => i >= 0 && i < points.length && points[i].rows !== null && points[i].rows > 0;
  points.forEach((p, i) => {
    if (loads(i) && !loads(i - 1) && !loads(i + 1)) {
      svg.appendChild(svgNode('circle', { cx: x(i), cy: y(p.rows), r: 2.5 }, 'vol-dot'));
    }
  });

  points.forEach((p, i) => {
    if (p.rows === 0) {
      const z = svgNode('text', { x: x(i), y: y(0) - 2, 'text-anchor': 'middle' }, 'vol-zero');
      z.textContent = '✕';
      svg.appendChild(z);
    }
  });

  // Endpoint: the latest day with data, ringed, with its value - the one direct label.
  let last = -1;
  for (let i = n - 1; i >= 0; i--) if (points[i].rows > 0) { last = i; break; }
  if (last >= 0) {
    svg.appendChild(svgNode('circle', { cx: x(last), cy: y(points[last].rows), r: 3.5 }, 'vol-dot'));
    const lab = svgNode('text', { x: x(last) + 7, y: y(points[last].rows) + 4 }, 'vol-endlabel');
    lab.textContent = fmtCompact(points[last].rows);
    svg.appendChild(lab);
  }

  // Ends of the time axis only - a label per day collides at any width.
  if (n) {
    const a = svgNode('text', { x: padL, y: H - 4, 'text-anchor': 'start' }, 'vol-axis');
    a.textContent = dayLabel(points[0].date);
    svg.appendChild(a);
    if (n > 1) {
      const b = svgNode('text', { x: W - padR, y: H - 4, 'text-anchor': 'end' }, 'vol-axis');
      b.textContent = dayLabel(points[n - 1].date);
      svg.appendChild(b);
    }
  }

  // Crosshair: snaps to the nearest day; the reader aims at a date, not a 2px line.
  const cross = svgNode('line', { y1: padT, y2: padT + plotH, visibility: 'hidden' }, 'vol-cross');
  const dot = svgNode('circle', { r: 4, visibility: 'hidden' }, 'vol-dot');
  svg.append(cross, dot);

  const hit = svgNode('rect', { x: padL, y: 0, width: plotW, height: H, tabindex: 0 }, 'vol-hit');
  const tip = $('#volTip');
  let focusIdx = n - 1;

  const show = (i, clientX, clientY) => {
    const p = points[i];
    cross.setAttribute('x1', x(i)); cross.setAttribute('x2', x(i));
    cross.setAttribute('visibility', 'visible');
    if (p.rows !== null) {
      dot.setAttribute('cx', x(i)); dot.setAttribute('cy', y(p.rows));
      dot.setAttribute('visibility', 'visible');
    } else {
      dot.setAttribute('visibility', 'hidden');
    }

    // Values lead, labels follow. Built with textContent only.
    tip.replaceChildren();
    tip.appendChild(el('div', 'v', p.rows === null ? 'no successful run' : `${fmtNumber(p.rows)} rows`));
    tip.appendChild(el('div', 'l', `${label} · ${dayLabel(p.date, true)}`));
    if (p.rows === 0) tip.appendChild(el('div', 'z', '✕ succeeded with 0 rows'));
    const runs = [];
    if (p.succeededRuns) runs.push(`${p.succeededRuns} succeeded`);
    if (p.failedRuns) runs.push(`${p.failedRuns} failed`);
    if (runs.length) tip.appendChild(el('div', 'l', `runs: ${runs.join(', ')}`));
    tip.hidden = false;

    const r = svg.getBoundingClientRect();
    const px = clientX ?? (r.left + (x(i) / W) * r.width);
    const py = clientY ?? (r.top + 20);
    const tw = tip.offsetWidth || 170;
    tip.style.left = `${Math.min(window.innerWidth - tw - 8, px + 14)}px`;
    tip.style.top = `${Math.max(8, py - 10)}px`;
  };
  const hide = () => {
    cross.setAttribute('visibility', 'hidden');
    dot.setAttribute('visibility', 'hidden');
    tip.hidden = true;
  };
  const nearest = (clientX) => {
    const r = svg.getBoundingClientRect();
    const sx = ((clientX - r.left) / r.width) * W;
    return Math.max(0, Math.min(n - 1, Math.round(((sx - padL) / Math.max(1, plotW)) * (n - 1))));
  };

  hit.addEventListener('pointermove', (e) => { focusIdx = nearest(e.clientX); show(focusIdx, e.clientX, e.clientY); });
  hit.addEventListener('pointerleave', hide);
  // Same readout from the keyboard: focus the chart, then arrow through the days.
  hit.addEventListener('focus', () => show(focusIdx));
  hit.addEventListener('blur', hide);
  hit.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowLeft') { focusIdx = Math.max(0, focusIdx - 1); show(focusIdx); e.preventDefault(); }
    if (e.key === 'ArrowRight') { focusIdx = Math.min(n - 1, focusIdx + 1); show(focusIdx); e.preventDefault(); }
  });
  svg.appendChild(hit);

  host.appendChild(svg);
}

/** The table twin: every value reachable without hovering. Newest day first. */
function renderVolumeTable(d) {
  const thead = $('#volTable thead');
  const tbody = $('#volTable tbody');
  const hr = el('tr');
  hr.appendChild(el('th', null, 'Day'));
  for (const s of d.series) hr.appendChild(el('th', 'num', s.label));
  hr.appendChild(el('th', 'num', 'All ten'));
  thead.replaceChildren(hr);

  tbody.replaceChildren();
  for (let i = d.total.length - 1; i >= 0; i--) {
    const tr = el('tr');
    tr.appendChild(tdWith(dayLabel(d.total[i].date, true)));
    const cell = (p) => {
      const td = tdWith(p.rows === null ? '—' : fmtNumber(p.rows), 'num');
      if (p.rows === 0) td.appendChild(el('span', 'vol-zero-key', ' ✕'));
      return td;
    };
    for (const s of d.series) tr.appendChild(cell(s.points[i]));
    tr.appendChild(cell(d.total[i]));
    tbody.appendChild(tr);
  }
}

// ---- AdHoc HAR lists ------------------------------------------------------

/**
 * key: the selected list. lists: the last summary. pending: the exact input that was
 * previewed - Commit sends THAT, so what is written is what was reviewed, even if the
 * text box has been edited since.
 */
const adhoc = { key: null, lists: [], pending: null };

function adhocMode() {
  return document.querySelector('input[name="adhocMode"]:checked')?.value || 'append';
}

function renderAdhocRunning() {
  const host = $('#adhocRunning');
  if (!host) return;
  host.replaceChildren(state.isRunning
    ? el('div', 'banner err',
        `The extract is running on ${state.env}. Lists can be previewed but not changed until it `
        + 'finishes - anything written now is either read too late or replaced by step 14.')
    : '');
  updateAdhocCommit();
}

async function refreshAdhoc() {
  const tbody = $('#adhocTable tbody');
  dim(tbody, true);

  let panel;
  try {
    panel = await api(`/api/adhoc?env=${encodeURIComponent(state.env)}`);
  } catch (e) {
    dim(tbody, false); tbody.replaceChildren(errorRow(5, e.message)); return;
  }
  dim(tbody, false);
  if (panel.error) { tbody.replaceChildren(errorRow(5, panel.error)); return; }

  adhoc.lists = panel.data;
  tbody.replaceChildren();
  for (const l of adhoc.lists) {
    const tr = el('tr');
    tr.dataset.key = l.key;
    if (l.key === adhoc.key) tr.classList.add('selected');

    const name = l.fileName.replace(/^AdhocHARList - /, '').replace(/\.csv$/i, '');
    const entries = l.exists ? fmtNumber(l.entries) : '—';
    const changed = l.exists ? fmtDateTime(l.modifiedLocal) : '';

    const note = tdWith('');
    if (!l.exists) {
      note.appendChild(el('span', 'flag warn', l.error ? 'unavailable' : 'file missing'));
      note.title = l.error || '';
    } else if (/Encounter$/.test(name)) {
      // The two lists step 14 replaces near the end of each run.
      note.appendChild(el('span', 'sub', 'step 14 refreshes'));
    }

    tr.append(tdWith(name), tdWith(l.columns.join(' | '), 'mono'),
              tdWith(entries, 'num'), tdWith(changed), note);
    tr.onclick = () => selectAdhoc(l.key);
    tbody.appendChild(tr);
  }

  if (adhoc.key) await loadAdhocDetail();
  else $('#adhocDetail').hidden = true;
}

async function selectAdhoc(key) {
  if (adhoc.key !== key) resetAdhocForm();
  adhoc.key = key;
  for (const tr of document.querySelectorAll('#adhocTable tbody tr')) {
    tr.classList.toggle('selected', tr.dataset.key === key);
  }
  $('#adhocSearch').value = '';
  await loadAdhocDetail();
}

async function loadAdhocDetail(search) {
  const host = $('#adhocDetail');
  const l = adhoc.lists.find((x) => x.key === adhoc.key);
  if (!l) { host.hidden = true; return; }
  host.hidden = false;

  const name = l.fileName.replace(/\.csv$/i, '');
  $('#adhocTitle').textContent = name;
  if (l.columns.length === 1) {
    $('#adhocFormatHint').textContent = 'One HSP_ACCOUNT_ID (10 digits) per line. A header line is optional.';
    $('#adhocInput').placeholder = '1234567890\n1234567891';
  } else {
    const tx = l.columns[1];
    const digits = tx === 'TX_ID' ? 9 : 15;
    const example = tx === 'TX_ID' ? '111111111' : '222222222222222';
    $('#adhocFormatHint').textContent =
      `One entry per line. Paste just the HSP_ACCOUNT_IDs (10 digits) and each is written as `
      + `"1234567890|" - the whole encounter. Where you have the ${tx} (${digits} digits), put it after `
      + `the encounter: "1234567890|${example}". Pipe, comma or tab between them all work - `
      + 'pasting two Excel columns gives a tab. The two kinds can be mixed. A header line is optional.';
    $('#adhocInput').placeholder = `1234567890\n1234567891\n1234567892|${example}`;
  }
  updateAdhocCommit();

  if (!l.exists) {
    $('#adhocMeta').textContent = l.error || 'The file is not there.';
    $('#adhocTail').textContent = '';
    $('#adhocTailLabel').textContent = '';
    $('#adhocMatches').replaceChildren();
    return;
  }

  const q = search ? `&q=${encodeURIComponent(search)}` : '';
  let panel;
  try {
    panel = await api(`/api/adhoc/${encodeURIComponent(l.key)}?env=${encodeURIComponent(state.env)}${q}`);
  } catch (e) { panelError($('#adhocMatches'), e.message); return; }
  if (panel.error) { panelError($('#adhocMatches'), panel.error); return; }

  const d = panel.data;
  $('#adhocMeta').textContent =
    `${fmtNumber(d.info.entries)} entr${d.info.entries === 1 ? 'y' : 'ies'} · header `
    + `"${d.info.header}" · last changed ${fmtDateTime(d.info.modifiedLocal)}`;

  const matches = $('#adhocMatches');
  if (d.search) {
    const box = el('div', 'upsert-note');
    if (!d.matchCount) {
      box.textContent = `"${d.search}" is not in this list.`;
    } else {
      box.textContent = `${fmtNumber(d.matchCount)} entr${d.matchCount === 1 ? 'y contains' : 'ies contain'} "${d.search}"`
        + (d.matchCount > d.matches.length ? ` - first ${d.matches.length} shown:` : ':');
      const pre = el('pre', 'adhoc-tail mono', d.matches.join('\n'));
      pre.style.maxHeight = '140px';
      box.appendChild(pre);
    }
    matches.replaceChildren(box);
  } else {
    matches.replaceChildren();
  }

  $('#adhocTailLabel').textContent = d.info.entries === 0
    ? 'The list is empty - only its header line.'
    : d.info.entries > d.tail.length
      ? `Last ${d.tail.length} of ${fmtNumber(d.info.entries)} entries (appends go at the bottom):`
      : `All ${fmtNumber(d.info.entries)} entr${d.info.entries === 1 ? 'y' : 'ies'}:`;
  $('#adhocTail').textContent = d.tail.join('\n');
}

function resetAdhocForm() {
  adhoc.pending = null;
  const input = $('#adhocInput');
  if (input) input.value = '';
  const file = $('#adhocFile');
  if (file) file.value = '';
  const res = $('#adhocResult');
  if (res) res.replaceChildren();
  updateAdhocCommit();
}

function updateAdhocCommit() {
  const btn = $('#adhocCommit');
  if (!btn) return;
  const p = adhoc.pending;
  const mode = adhocMode();
  const ready = p && p.result.success && p.mode === mode && p.key === adhoc.key && p.env === state.env;
  btn.disabled = !ready || state.isRunning || !state.actionsEnabled;
  btn.className = mode === 'remove' ? 'danger' : 'primary';
  btn.textContent = ready
    ? `${mode === 'remove' ? 'Remove' : 'Append'} ${fmtNumber(p.result.changeCount)} entr${p.result.changeCount === 1 ? 'y' : 'ies'}`
    : (mode === 'remove' ? 'Remove' : 'Append');
  btn.title = state.isRunning ? 'Not while the extract is running.'
            : !state.actionsEnabled ? 'This console was started read-only.'
            : !ready ? 'Preview first.' : '';
}

/** Builds the request body from the text box or the chosen file. */
async function adhocRequest() {
  const file = $('#adhocFile').files[0];
  if (file) {
    if (/\.xlsx?$/i.test(file.name)) {
      const buf = new Uint8Array(await file.arrayBuffer());
      let bin = '';
      for (let i = 0; i < buf.length; i += 0x8000) {
        bin += String.fromCharCode.apply(null, buf.subarray(i, i + 0x8000));
      }
      return { fileName: file.name, fileBase64: btoa(bin), text: null };
    }
    return { fileName: file.name, fileBase64: null, text: await file.text() };
  }
  return { fileName: null, fileBase64: null, text: $('#adhocInput').value };
}

async function previewAdhoc() {
  if (!adhoc.key) return;
  const mode = adhocMode();
  const body = { environment: state.env, ...(await adhocRequest()), commit: false, confirm: null };
  if (!body.text && !body.fileBase64) { toast('Type, paste or upload some entries first.', false); return; }

  let r;
  try {
    r = await api(`/api/adhoc/${encodeURIComponent(adhoc.key)}/${mode}`, {
      method: 'POST', body: JSON.stringify(body),
    });
  } catch (e) { toast(e.message, false); return; }

  adhoc.pending = { key: adhoc.key, env: state.env, mode, body, result: r };
  renderAdhocResult(r, false);
  updateAdhocCommit();
}

async function commitAdhoc() {
  const p = adhoc.pending;
  if (!p || !p.result.success) return;

  const prod = state.envInfo.isProduction;
  const l = adhoc.lists.find((x) => x.key === p.key);
  const verb = p.mode === 'remove' ? 'Remove' : 'Append';
  let body = `${verb} ${fmtNumber(p.result.changeCount)} entr${p.result.changeCount === 1 ? 'y' : 'ies'} `
           + `${p.mode === 'remove' ? 'from' : 'to'} ${l ? l.fileName : p.key} on ${state.env}. `
           + `The list goes from ${fmtNumber(p.result.entriesBefore)} to ${fmtNumber(p.result.entriesAfter)} entries, `
           + 'and the next extract re-pulls whatever is in it.';
  if (prod) body = `This is PRODUCTION.\n\n${body}`;

  const ok = await confirmDialog({
    title: `${verb} ${p.mode === 'remove' ? 'from' : 'to'} the AdHoc list?`,
    body,
    typeToConfirm: prod ? state.env : null,
  });
  if (!ok) return;

  let r;
  try {
    r = await api(`/api/adhoc/${encodeURIComponent(p.key)}/${p.mode}`, {
      method: 'POST',
      // Exactly what was previewed - the server re-validates it all anyway.
      body: JSON.stringify({ ...p.body, commit: true, confirm: prod ? ok : null }),
    });
  } catch (e) { toast(e.message, false); return; }

  toast(r.message, r.success && r.committed);
  renderAdhocResult(r, true);
  if (r.committed) {
    adhoc.pending = null;
    $('#adhocInput').value = '';
    $('#adhocFile').value = '';
    await refreshAdhoc();
  }
  updateAdhocCommit();
}

function renderAdhocResult(r, committed) {
  const host = $('#adhocResult');
  const ok = r.success;
  const box = el('div', `banner stack${ok ? '' : ' err'}`);
  box.appendChild(el('div', null, r.message));

  const counts = el('div', 'adhoc-counts');
  const add = (label, n) => {
    if (!n) return;
    const s = el('span');
    s.append(el('strong', null, fmtNumber(n)), document.createTextNode(` ${label}`));
    counts.appendChild(s);
  };
  add(r.mode === 'remove' ? (committed ? 'removed' : 'to remove') : (committed ? 'appended' : 'to append'), r.changeCount);
  add('already in the list - skipped', r.alreadyPresent);
  add('not in the list - skipped', r.notFound);
  add('repeated in the input - counted once', r.duplicateInInput);
  add('invalid', r.rejected.length);
  if (counts.childNodes.length) box.appendChild(counts);

  if (r.headerSkipped) {
    box.appendChild(el('div', 'sub', `First line taken as a header and skipped: "${r.headerSkipped}"`));
  }

  if (r.rejected.length) {
    const ul = el('ul', 'adhoc-rejects');
    for (const x of r.rejected.slice(0, 200)) {
      const li = el('li');
      li.append(el('span', 'mono', x.line ? `line ${x.line}: ` : ''),
                el('span', 'mono', x.value), document.createTextNode(` — ${x.reason}`));
      ul.appendChild(li);
    }
    box.appendChild(ul);
    if (r.rejected.length > 200) box.appendChild(el('div', 'sub', `…and ${r.rejected.length - 200} more.`));
  }

  if (!committed && ok && r.sample.length) {
    // Shown exactly as they will be written, so an encounter on its own visibly gains
    // its trailing pipe before anything is committed.
    box.appendChild(el('div', 'sub',
      r.changeCount > r.sample.length
        ? `First ${r.sample.length} of them, exactly as they will be written:`
        : 'Exactly as they will be written:'));
    const pre = el('pre', 'adhoc-tail mono', r.sample.join('\n'));
    pre.style.maxHeight = '140px';
    box.appendChild(pre);
  }
  host.replaceChildren(box);
}

function wireAdhoc() {
  $('#adhocPreview').onclick = previewAdhoc;
  $('#adhocCommit').onclick = commitAdhoc;
  $('#adhocClearInput').onclick = resetAdhocForm;
  // Any change to the input invalidates the preview - you commit what you reviewed.
  $('#adhocInput').oninput = () => { adhoc.pending = null; updateAdhocCommit(); };
  $('#adhocFile').onchange = () => {
    adhoc.pending = null;
    const f = $('#adhocFile').files[0];
    if (f) $('#adhocInput').value = '';
    $('#adhocResult').replaceChildren(f ? el('div', 'sub', `Will read ${f.name}. Press Preview.`) : '');
    updateAdhocCommit();
  };
  for (const r of document.querySelectorAll('input[name="adhocMode"]')) {
    r.onchange = () => { adhoc.pending = null; $('#adhocResult').replaceChildren(); updateAdhocCommit(); };
  }
  $('#adhocSearchBtn').onclick = () => loadAdhocDetail($('#adhocSearch').value.trim());
  $('#adhocSearch').onkeydown = (e) => { if (e.key === 'Enter') loadAdhocDetail($('#adhocSearch').value.trim()); };
}

// ---- diagnostics ----------------------------------------------------------

async function refreshDiagnostics() {
  const summary = $('#diagSummary');
  const failures = $('#failuresTable tbody');
  const rules = $('#rulesTable tbody');
  dim(failures, true); dim(rules, true);

  let panel;
  try {
    panel = await api(`/api/diagnostics?env=${encodeURIComponent(state.env)}`);
  } catch (e) {
    dim(failures, false); dim(rules, false);
    summary.replaceChildren();
    failures.replaceChildren(errorRow(6, e.message));
    rules.replaceChildren(errorRow(6, e.message));
    return;
  }
  dim(failures, false); dim(rules, false);

  if (panel.error) {
    summary.replaceChildren(el('div', 'panel-error',
      panel.error + ' — if the catalogue has not been installed yet, run '
      + 'sql/20_install_diagnostics.sql on this environment’s control instance.'));
    failures.replaceChildren(); rules.replaceChildren();
    return;
  }

  const d = panel.data;

  // Group identical errors so the list is readable, but keep the occurrence count so
  // the headline numbers can actually be reconciled against what is on screen.
  const groups = new Map();
  for (const f of d.recentFailures) {
    const key = (f.ruleId ? `r${f.ruleId}|` : 'u|') + (f.message || '').replace(/\s+/g, ' ').slice(0, 160);
    const g = groups.get(key);
    if (g) { g.count += 1; if (!g.last || f.whenLocal > g.last) g.last = f.whenLocal; }
    else groups.set(key, { f, count: 1, last: f.whenLocal });
  }
  const all = [...groups.values()].sort((a, b) => (b.last || '').localeCompare(a.last || ''));

  const line = el('div', 'hero-sub');
  line.textContent = `${d.rules.length} rule${d.rules.length === 1 ? '' : 's'} · `
    + `${d.total} failure${d.total === 1 ? '' : 's'} in the last 90 days, `
    + `${all.length} distinct · ${d.explained} explained · ${d.unexplained} not yet`;
  summary.replaceChildren(line);

  // ---- filter chips. Every count here is reachable by clicking it.
  const counts = {
    all: d.total,
    explained: d.explained,
    unexplained: d.unexplained,
  };
  const labels = {
    all: 'All',
    explained: 'Explained',
    unexplained: 'Not yet explained',
  };
  const filterRow = $('#diagFilter');
  filterRow.replaceChildren();
  for (const k of ['all', 'explained', 'unexplained']) {
    const b = el('button', state.diagFilter === k ? 'primary' : null, `${labels[k]} ${counts[k]}`);
    b.disabled = counts[k] === 0 && k !== 'all';
    b.onclick = () => { state.diagFilter = k; renderFailureRows(all); paintFilter(filterRow, counts, labels); };
    filterRow.appendChild(b);
  }

  renderFailureRows(all);

  // ---- the catalogue, with how often each rule actually fired
  const hits = {};
  for (const f of d.recentFailures) {
    if (f.ruleId) hits[f.ruleId] = (hits[f.ruleId] || 0) + 1;
  }

  rules.replaceChildren();
  for (const r of d.rules) {
    const tr = el('tr');
    const title = el('td');
    title.appendChild(el('div', null, r.title));
    if (r.cause) {
      const hint = el('div', 'sub');
      hint.textContent = r.cause.length > 150 ? r.cause.slice(0, 150) + '…' : r.cause;
      title.appendChild(hint);
    }
    title.appendChild(buildSolutions(r.solutions, r.ruleId));
    if (!r.isEnabled) title.appendChild(el('span', 'flag', 'disabled'));

    const prio = tdWith(String(r.priority), 'num');
    prio.title = 'Lowest wins when several rules match the same error.';

    tr.append(
      tdWith(`#${r.ruleId}`, 'num'),
      prio,
      title,
      tdWith(r.pattern, 'mono'),
      tdWith(hits[r.ruleId] ? String(hits[r.ruleId]) : '—', 'num'),
      tdWith(r.addedBy || '—'),
    );
    rules.appendChild(tr);
  }
}

function paintFilter(row, counts, labels) {
  const keys = ['all', 'explained', 'unexplained'];
  [...row.children].forEach((b, i) => {
    b.className = state.diagFilter === keys[i] ? 'primary' : '';
    b.textContent = `${labels[keys[i]]} ${counts[keys[i]]}`;
  });
}

/** Renders the grouped failures under the current filter. */
function renderFailureRows(groups) {
  const tbody = $('#failuresTable tbody');
  const shown = groups.filter((g) =>
    state.diagFilter === 'all'
    || (state.diagFilter === 'explained' && g.f.ruleId)
    || (state.diagFilter === 'unexplained' && !g.f.ruleId));

  tbody.replaceChildren();

  if (!shown.length) {
    const tr = el('tr');
    const td = el('td', 'empty', state.diagFilter === 'unexplained'
      ? 'Every recorded failure matches a rule.'
      : 'Nothing to show.');
    td.colSpan = 6; tr.appendChild(td); tbody.appendChild(tr);
    return;
  }

  for (const g of shown) {
    const f = g.f;
    const tr = el('tr');

    const msg = el('td');
    msg.appendChild(el('div', 'mono', shortenError(f.message)));

    const explain = el('td');
    if (f.ruleId) {
      const t = el('div', null, f.title);
      explain.appendChild(t);
      // A count here, not the list: with 16 rows the full lists make the table
      // unscannable. One click opens it, and the failing step shows it unprompted.
      const n = (f.solutions || []).length;
      const toggle = el('button', 'ghost',
        n ? `${n} thing${n === 1 ? '' : 's'} to try` : 'no fixes recorded');
      const holder = el('div');
      toggle.onclick = () => {
        if (holder.firstChild) { holder.replaceChildren(); return; }
        holder.appendChild(buildSolutions(f.solutions, f.ruleId));
      };
      explain.append(toggle, holder, el('div', 'sub', `rule #${f.ruleId}`));
    } else if (state.actionsEnabled) {
      const b = el('button', 'ghost', 'Add rule');
      b.onclick = () => openRuleDialog(f);
      explain.appendChild(b);
    } else {
      explain.appendChild(el('span', 'sub', 'no rule yet'));
    }

    tr.append(
      tdWith(g.count > 1 ? `× ${g.count}` : '1', 'num'),
      tdWith(fmtDateTime(g.last)),
      tdWith(f.jobName),
      tdWith(`${f.stepId} · ${f.stepName}`),
      msg,
      explain,
    );
    tbody.appendChild(tr);
  }
}

/**
 * The ordered list of things to try. Rendered as a numbered list rather than prose so
 * it reads as a checklist under pressure, and so "try this first" is visible.
 */
function buildSolutions(solutions, ruleId) {
  const wrap = el('div', 'dx-solutions');

  if (!solutions || !solutions.length) {
    const none = el('div', 'dx-body', 'No fixes recorded for this one yet.');
    if (state.actionsEnabled && ruleId) {
      const b = el('button', 'ghost', 'Add one');
      b.onclick = (ev) => { ev.stopPropagation(); openSolutionDialog(ruleId); };
      none.appendChild(b);
    }
    wrap.appendChild(none);
    return wrap;
  }

  wrap.appendChild(el('div', 'dx-what', solutions.length === 1 ? 'What to do' : 'Things to try, in order'));

  const ol = el('ol', 'dx-list');
  for (const s of solutions) {
    const li = el('li');
    li.appendChild(document.createTextNode(s.solution));
    if (s.isVerified) {
      const v = el('span', 'flag', 'verified');
      v.title = 'Someone confirmed this actually resolved the failure.';
      li.appendChild(v);
    }
    if (s.detail) li.appendChild(el('div', 'sub', s.detail));
    ol.appendChild(li);
  }
  wrap.appendChild(ol);

  if (state.actionsEnabled && ruleId) {
    const b = el('button', 'ghost', '+ Add a fix to this rule');
    b.title = 'Append something that worked to this rule, for everyone. It does not change the existing entries.';
    b.onclick = (ev) => { ev.stopPropagation(); openSolutionDialog(ruleId); };
    wrap.appendChild(b);
  }
  return wrap;
}

function openSolutionDialog(ruleId) {
  const dlg = $('#solutionDlg');
  $('#solutionRule').textContent = `Rule #${ruleId}`;
  $('#solutionText').value = '';

  $('#solutionCancel').onclick = () => dlg.close();
  $('#solutionSave').onclick = async () => {
    try {
      const r = await api(`/api/diagnostics/rules/${ruleId}/solutions`, {
        method: 'POST',
        body: JSON.stringify({ environment: state.env, solutions: $('#solutionText').value }),
      });
      toast(r.message, r.success);
      if (r.success) { dlg.close(); refreshDiagnostics(); refreshStatus(); }
    } catch (e) {
      toast(e.message, false);
    }
  };
  dlg.showModal();
  $('#solutionText').focus();
}

/** SQL Agent wraps the real error in boilerplate; show the useful middle. */
function shortenError(message) {
  if (!message) return '—';
  const flat = message.replace(/\s+/g, ' ').trim();
  const markers = ['The error information returned by PowerShell is:', 'Description:',
                   'Unable to start execution', 'One or both child jobs'];
  for (const m of markers) {
    const i = flat.indexOf(m);
    if (i >= 0) return flat.slice(i, i + 190);
  }
  return flat.length > 190 ? flat.slice(0, 190) + '…' : flat;
}

function openRuleDialog(failure) {
  const dlg = $('#ruleDlg');
  const src = $('#ruleSourceWrap');

  if (failure) {
    src.style.display = '';
    src.textContent = `${failure.jobName} · step ${failure.stepId} · ${failure.stepName}`;
    // Pre-fill with a distinctive slice of the real error rather than the whole thing,
    // which is mostly boilerplate that would never match twice.
    $('#rulePattern').value = suggestPattern(failure.message);
    $('#ruleTitle').value = '';
  } else {
    src.style.display = 'none';
    $('#rulePattern').value = '';
    $('#ruleTitle').value = '';
  }
  $('#ruleCause').value = '';
  $('#ruleAction').value = '';

  $('#ruleCancel').onclick = () => dlg.close();
  $('#ruleSave').onclick = async () => {
    const body = {
      environment: state.env,
      pattern: $('#rulePattern').value,
      matchType: 'Contains',
      title: $('#ruleTitle').value,
      cause: $('#ruleCause').value,
      solutions: $('#ruleAction').value,
      priority: 100,
    };
    try {
      const r = await api('/api/diagnostics/rules', { method: 'POST', body: JSON.stringify(body) });
      toast(r.message, r.success);
      if (r.success) { dlg.close(); refreshDiagnostics(); refreshStatus(); }
    } catch (e) {
      toast(e.message, false);
    }
  };

  dlg.showModal();
  $('#ruleTitle').focus();
}

/** A first guess at a pattern: the distinctive sentence, not the Agent boilerplate. */
function suggestPattern(message) {
  if (!message) return '';
  const flat = message.replace(/\s+/g, ' ').trim();
  const m = flat.match(/returned by PowerShell is: '([^']{10,120})/)
         || flat.match(/(One or both child jobs[^.\[]{0,80})/)
         || flat.match(/(Unable to start execution of step \d+)/)
         || flat.match(/((?:Database|Login failed|Cannot find path|Access to the path)[^.\[]{0,80})/);
  return m ? m[1].trim() : flat.slice(0, 80);
}

// ---------------------------------------------------------------------------

boot();
