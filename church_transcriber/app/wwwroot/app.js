'use strict';
// Church Service Transcriber - single-page front end.
// All URLs are relative so the app works behind Home Assistant ingress (/api/hassio_ingress/<token>/).

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const view = $('#view');
let STATUS = null;
let timers = [];
let leaveGuard = null;

const KINDS = {
  prelude: 'Pre-service', welcome: 'Welcome', worship: 'Worship', prayer: 'Prayer', announcements: 'Announcements',
  scripture: 'Scripture Reading', sermon: 'Sermon', offering: 'Offering', communion: 'Communion', baptism: 'Baptism',
  testimony: 'Testimony', response: 'Response & Altar Call', benediction: 'Benediction & Closing', other: 'Other'
};

async function api(path, opt = {}) {
  const init = { method: opt.method || 'GET', headers: {} };
  if (opt.body !== undefined) { init.body = JSON.stringify(opt.body); init.headers['Content-Type'] = 'application/json'; }
  const res = await fetch('api/' + path, init);
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) throw new Error((data && data.error) || `Request failed (${res.status})`);
  return data;
}

function toast(msg, err = false) {
  const t = $('#toast');
  t.textContent = msg; t.className = 'toast show' + (err ? ' err' : '');
  clearTimeout(toast._t); toast._t = setTimeout(() => t.className = 'toast', 3200);
}

const fmtTime = s => {
  s = Math.max(0, Math.floor(s || 0));
  const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), sec = s % 60;
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}` : `${m}:${String(sec).padStart(2, '0')}`;
};
const fmtDate = d => d ? new Date(d).toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) : '';
const langName = c => (STATUS?.languages?.[c]) || c;
const every = (fn, ms) => timers.push(setInterval(fn, ms));
const ACTIVE = ['queued', 'downloading', 'transcribing', 'sectioning'];

function statusPill(t) {
  const s = t.status;
  const cls = s === 'ready' ? 'ready' : s === 'failed' ? 'failed' : s === 'waiting' ? 'waiting' : ACTIVE.includes(s) ? 'active' : '';
  const label = { queued: 'Queued', waiting: t.liveStatus === 'is_live' ? 'Live now' : 'Scheduled', downloading: 'Downloading',
    transcribing: 'Transcribing', sectioning: 'Organising', ready: 'Ready', failed: 'Failed', cancelled: 'Cancelled' }[s] || s;
  return `<span class="pill ${cls}">${label}</span>`;
}

// ---------------------------------------------------------------- Router
async function route() {
  if (leaveGuard && !leaveGuard()) return;
  leaveGuard = null;
  timers.forEach(clearInterval); timers = [];
  document.querySelector('.player')?.remove();
  const h = location.hash.replace(/^#\/?/, '');
  const [page, id] = h.split('/');
  $$('.nav a').forEach(a => a.classList.toggle('active', a.dataset.nav === (page || 'library') || (page === 't' && a.dataset.nav === 'library')));
  if (!STATUS) await loadStatus();
  window.scrollTo(0, 0);
  try {
    if (page === 'new') return viewNew();
    if (page === 'watches') return viewWatches();
    if (page === 'settings') return viewSettings();
    if (page === 'api') return viewApi(id);
    if (page === 't' && id) return viewEditor(id);
    return viewLibrary();
  } catch (e) { view.innerHTML = `<div class="empty"><h2>Something went wrong</h2><p>${esc(e.message)}</p></div>`; }
}
async function loadStatus() {
  try { STATUS = await api('status'); } catch { STATUS = { languages: { en: 'English' }, kinds: KINDS, ollama: {} }; }
}
window.addEventListener('hashchange', route);

// ---------------------------------------------------------------- Library
async function viewLibrary() {
  view.innerHTML = `
    <h1 class="page">Library</h1>
    <p class="sub">Your transcribed services. New ones appear here while they process.</p>
    <div class="lib-tools"><input type="search" id="q" placeholder="Search titles or channels…"><a class="btn primary" href="#/new">+ Add service</a></div>
    <div id="list" class="list"></div>`;
  let items = [];
  const draw = () => {
    const q = $('#q').value.toLowerCase();
    const shown = items.filter(t => !q || (t.title + ' ' + (t.channel || '')).toLowerCase().includes(q));
    if (!items.length) {
      $('#list').innerHTML = `<div class="card empty"><h2>No services yet</h2><p>Paste a YouTube link to transcribe a service, or watch your church's channel so new services are picked up automatically.</p>
        <div class="row" style="justify-content:center"><a class="btn primary" href="#/new">Add a service</a><a class="btn" href="#/watches">Watch a channel</a></div></div>`;
      return;
    }
    $('#list').innerHTML = shown.map(t => {
      const msg = t.status === 'failed' ? `<div class="msg err">${esc(t.error || 'Failed')}</div>`
        : t.status === 'waiting' ? `<div class="msg">${t.scheduledStart ? 'Starts ' + esc(fmtDate(t.scheduledStart)) + ' · ' : ''}${esc(t.statusMessage || '')}</div>`
        : t.status !== 'ready' ? `<div class="msg">${esc(t.statusMessage || '')}</div>` : '';
      const bar = ACTIVE.includes(t.status) ? `<div class="bar"><i style="width:${Math.round((t.progress || 0) * 100)}%"></i></div>` : '';
      const tr = (t.translations || []).map(x => `<span class="pill">${esc(x.lang.toUpperCase())}${x.status === 'done' ? '' : ' …'}</span>`).join(' ');
      return `<a class="card item" href="#/t/${t.id}">
        <div class="thumb" style="background-image:url('${esc(t.thumbnail || '')}')">${t.duration ? `<span class="dur">${fmtTime(t.duration)}</span>` : ''}</div>
        <div><h3>${esc(t.title)}</h3>
          <div class="meta">${statusPill(t)}${t.channel ? `<span>${esc(t.channel)}</span>` : ''}${t.uploadDate ? `<span>${esc(t.uploadDate)}</span>` : ''}
          ${t.sectionCount ? `<span>${t.sectionCount} sections</span>` : ''}${tr}</div>${msg}${bar}</div></a>`;
    }).join('') || '<p class="muted">No matches.</p>';
  };
  const load = async () => { items = await api('transcripts'); draw(); };
  $('#q').addEventListener('input', draw);
  await load();
  every(() => { if (items.some(t => ACTIVE.includes(t.status) || t.status === 'waiting' || (t.translations || []).some(x => x.status !== 'done' && x.status !== 'error'))) load().catch(() => {}); }, 4000);
}

// ---------------------------------------------------------------- Shared bits
function langOptions(selected, includeAuto) {
  const opts = Object.entries(STATUS.languages || {}).map(([c, n]) => `<option value="${c}" ${c === selected ? 'selected' : ''}>${esc(n)}</option>`).join('');
  return (includeAuto ? `<option value="auto" ${selected === 'auto' ? 'selected' : ''}>Detect automatically</option>` : '') + opts;
}
function langChips(selected = []) {
  return `<div class="chips">${Object.entries(STATUS.languages || {}).map(([c, n]) =>
    `<span class="chip ${selected.includes(c) ? 'on' : ''}" data-lang="${c}">${esc(n)}</span>`).join('')}</div>`;
}
function wireChips(root) { root.addEventListener('click', e => { const c = e.target.closest('.chip'); if (c) c.classList.toggle('on'); }); }
const chosenChips = root => $$('.chip.on', root).map(c => c.dataset.lang);

// ---------------------------------------------------------------- Add service
async function viewNew() {
  const s = await api('settings');
  view.innerHTML = `
    <h1 class="page">Add a service</h1>
    <p class="sub">Paste one or more YouTube links. Scheduled premieres and live streams are fine - they wait and process automatically once the service has finished.</p>
    <div class="card pad" style="max-width:760px">
      <label class="field"><span>YouTube links</span><textarea id="urls" rows="4" placeholder="https://www.youtube.com/watch?v=…&#10;https://youtube.com/live/…"></textarea>
        <small>One per line.</small></label>
      <label class="field"><span>Spoken language</span><select id="lang">${langOptions(s.defaultLanguage || 'en', true)}</select></label>
      <div class="field"><span style="display:block;font-weight:600;margin-bottom:6px;font-size:14px">Also translate into</span><div id="tr">${langChips(s.autoTranslateTo || [])}</div>
        <small class="muted">Optional. Translations run on your local AI model after transcription.</small></div>
      <div class="row"><button class="btn primary" id="go">Transcribe</button><a class="btn" href="#/">Cancel</a></div>
    </div>`;
  wireChips($('#tr'));
  $('#go').onclick = async () => {
    const btn = $('#go'); btn.disabled = true;
    try {
      const r = await api('transcripts', { method: 'POST', body: { urls: $('#urls').value, language: $('#lang').value, translateTo: chosenChips($('#tr')) } });
      toast(r.ids.length === 1 ? 'Added - processing has started' : `Added ${r.ids.length} services`);
      location.hash = r.ids.length === 1 ? `#/t/${r.ids[0]}` : '#/';
    } catch (e) { toast(e.message, true); btn.disabled = false; }
  };
}

// ---------------------------------------------------------------- Editor
async function viewEditor(id) {
  let T = await api('transcripts/' + id);
  let lang = '';            // '' = original
  let dirty = false, saving = false, saveTimer = null, lastStatus = T.status;
  const trState = () => JSON.stringify(Object.values(T.translations || {}).map(x => [x.lang, x.status]));
  let lastTr = trState();

  leaveGuard = () => !dirty || confirm('You have unsaved changes. Leave anyway?');
  window.onbeforeunload = () => dirty ? true : undefined;

  function shell() {
    const tr = T.translations?.[lang];
    const title = lang && tr?.status === 'done' ? (tr.title || T.title) : T.title;
    view.innerHTML = `
      <div class="ed-head">
        <input class="ed-title" id="title" value="${esc(title)}" aria-label="Title" ${T.status === 'ready' ? '' : 'disabled'}>
        <div class="ed-meta">${statusPill(T)}
          ${T.channel ? `<span>${esc(T.channel)}</span>` : ''}${T.uploadDate ? `<span>${esc(T.uploadDate)}</span>` : ''}
          ${T.duration ? `<span>${fmtTime(T.duration)}</span>` : ''}
          <a href="${esc(T.url)}" target="_blank" rel="noopener">Watch on YouTube ↗</a>
          <span class="grow"></span>
          <button class="btn small side-toggle" id="sideBtn">Sections</button>
          <div class="menu" id="exportMenu"><button class="btn small">Export ▾</button>
            <div class="menu-list card">${exportLinks()}</div></div>
          <div class="menu" id="moreMenu"><button class="btn small">More ▾</button>
            <div class="menu-list card">
              ${T.status === 'ready' ? `<button data-act="api">Use in an automation (API)…</button>` : ''}
              ${T.status === 'ready' ? `<button data-act="resection">Re-organise sections automatically</button>` : ''}
              ${['failed', 'cancelled', 'ready'].includes(T.status) ? `<button data-act="retry">Download &amp; transcribe again</button>` : ''}
              ${[...ACTIVE, 'waiting'].includes(T.status) ? `<button data-act="cancel">Cancel processing</button>` : ''}
              <hr><button data-act="delete" class="danger" style="color:var(--bad)">Delete service</button>
            </div></div>
        </div>
      </div>
      <div id="body"></div>`;
    $('#title').addEventListener('input', markDirty);
    $('#sideBtn').onclick = () => $('.side')?.classList.toggle('open');
    $$('.menu').forEach(m => m.querySelector('.btn').onclick = e => { e.stopPropagation(); $$('.menu').forEach(x => x !== m && x.classList.remove('open')); m.classList.toggle('open'); });
    $('#moreMenu .menu-list').onclick = e => { const a = e.target.closest('[data-act]'); if (a) moreAction(a.dataset.act); };
    if (T.status === 'ready') renderDoc(); else renderProgress();
  }

  function exportLinks() {
    const q = lang ? `&lang=${lang}` : '';
    const base = `api/transcripts/${id}/export?`;
    return `<a href="${base}format=docx${q}" download>Word document (.docx)</a>
      <a href="${base}format=print${q}" target="_blank" rel="noopener">Print / save as PDF</a>
      <a href="${base}format=md${q}" download>Markdown (.md)</a>
      <a href="${base}format=txt${q}" download>Plain text (.txt)</a>
      <a href="${base}format=html${q}" download>Web page (.html)</a>
      ${lang ? '' : `<a href="${base}format=srt" download>Subtitles (.srt)</a>`}
      <hr><a href="${base}format=docx${q}&ts=0" download>Word document without timestamps</a>`;
  }

  function renderProgress() {
    const waiting = T.status === 'waiting';
    $('#body').innerHTML = `<div class="card status-box">
      <h2>${waiting ? (T.liveStatus === 'is_live' ? 'Service is live' : 'Scheduled service') : T.status === 'failed' ? 'Processing failed' : T.status === 'cancelled' ? 'Cancelled' : 'Working on it…'}</h2>
      ${waiting && T.scheduledStart ? `<p>Starts ${esc(fmtDate(T.scheduledStart))}</p>` : ''}
      <p class="${T.status === 'failed' ? '' : 'muted'}" style="${T.status === 'failed' ? 'color:var(--bad)' : ''}">${esc(T.status === 'failed' ? T.error : T.statusMessage || '')}</p>
      ${ACTIVE.includes(T.status) ? `<div class="bar"><i style="width:${Math.round((T.progress || 0) * 100)}%"></i></div>` : ''}
      ${waiting && T.nextCheck ? `<p class="muted">Next check ${esc(fmtDate(T.nextCheck))}</p>` : ''}
      ${['failed', 'cancelled'].includes(T.status) ? `<button class="btn primary" id="retryBtn">Try again</button>` : ''}
      <p class="muted" style="font-size:13px;margin-top:16px">You can close this page - processing continues on your Home Assistant.</p></div>`;
    $('#retryBtn') && ($('#retryBtn').onclick = () => moreAction('retry'));
  }

  function tabsHtml() {
    const trs = Object.values(T.translations || {});
    return `<div class="tabs" id="tabs">
      <button class="tab ${lang === '' ? 'on' : ''}" data-lang="">Original${T.detectedLanguage ? ' · ' + esc(langName(T.detectedLanguage)) : ''}</button>
      ${trs.map(x => `<button class="tab ${lang === x.lang ? 'on' : ''}" data-lang="${x.lang}">${esc(langName(x.lang))}${x.status === 'done' ? '' : x.status === 'error' ? '<span class="pct">failed</span>' : `<span class="pct">${Math.round((x.progress || 0) * 100)}%</span>`}</button>`).join('')}
      <button class="tab" id="addTr">+ Translate</button></div>`;
  }

  function sectionsFor() {
    if (!lang) return T.sections.map(s => ({ ...s }));
    const tr = T.translations[lang];
    const map = Object.fromEntries((tr.sections || []).map(s => [s.id, s]));
    return T.sections.map(s => ({ ...s, title: map[s.id]?.title ?? s.title, html: map[s.id]?.html ?? s.html, summary: map[s.id] ? null : s.summary }));
  }

  function secHtml(s) {
    const kindSel = `<select class="kind" ${lang ? 'disabled' : ''}>${Object.entries(KINDS).map(([k, n]) => `<option value="${k}" ${k === s.kind ? 'selected' : ''}>${esc(n)}</option>`).join('')}</select>`;
    return `<section class="sec k-${esc(s.kind)}" id="s-${s.id}" data-id="${s.id}" data-start="${s.start}" data-end="${s.end}" data-summary="${esc(s.summary || '')}">
      <div class="sec-head">${kindSel}<input class="stitle" value="${esc(s.title)}" aria-label="Section title">
        <span class="time" title="Play from here">${fmtTime(s.start)} – ${fmtTime(s.end)}</span>
        ${lang ? '' : `<span class="acts"><button data-op="split" title="Split this section at the cursor">Split</button><button data-op="merge" title="Merge with the section above">Merge ↑</button><button data-op="del" title="Delete section">✕</button></span>`}
      </div>
      ${s.summary ? `<div class="sec-summary">${esc(s.summary)}</div>` : ''}
      <div class="sec-body" contenteditable="true" spellcheck="true">${s.html}</div></section>`;
  }

  function renderDoc() {
    const tr = lang ? T.translations[lang] : null;
    let docHtml;
    if (tr && tr.status !== 'done') {
      docHtml = `<div class="card status-box"><h2>${tr.status === 'error' ? 'Translation failed' : 'Translating into ' + esc(langName(lang)) + '…'}</h2>
        ${tr.status === 'error' ? `<p style="color:var(--bad)">${esc(tr.error || '')}</p><button class="btn primary" id="trRetry">Try again</button>`
          : `<div class="bar"><i style="width:${Math.round((tr.progress || 0) * 100)}%"></i></div><p class="muted">Your local AI model is translating section by section.</p>`}
        <p><button class="btn small danger" id="trDel">Remove this translation</button></p></div>`;
    } else {
      const summary = tr ? tr.summaryHtml : T.summaryHtml;
      docHtml = `
        <div class="toolbar" id="tb">
          <button data-cmd="bold" title="Bold"><b>B</b></button><button data-cmd="italic" title="Italic"><i>I</i></button>
          <button data-cmd="underline" title="Underline"><u>U</u></button><button data-cmd="mark" title="Highlight">🖍</button>
          <span class="sep"></span>
          <button data-cmd="h2" title="Heading">H2</button><button data-cmd="h3" title="Sub-heading">H3</button><button data-cmd="p" title="Normal text">¶</button>
          <button data-cmd="blockquote" title="Quote">❝</button><button data-cmd="insertUnorderedList" title="Bullets">• List</button>
          <button data-cmd="insertOrderedList" title="Numbers">1. List</button><button data-cmd="removeFormat" title="Clear formatting">Tx</button>
          <span class="sep"></span>
          <button data-cmd="undo" title="Undo">↶</button><button data-cmd="redo" title="Redo">↷</button>
          <button id="findBtn" title="Find &amp; replace">Find</button><button id="tsBtn" title="Show/hide timestamps">⏱</button>
          <span class="save-state" id="saveState">Saved</span>
          <button class="btn small primary" id="saveBtn" style="height:30px">Save</button>
        </div>
        <div class="card findbar" id="findbar">
          <input type="text" id="fFind" placeholder="Find (e.g. a misheard name)"><input type="text" id="fRep" placeholder="Replace with">
          <label class="row" style="gap:4px;font-size:13px"><input type="checkbox" id="fCase"> Match case</label>
          <button class="btn small" id="fNext">Find</button><button class="btn small primary" id="fAll">Replace all</button>
          ${lang ? '' : '<button class="btn small" id="fVocab" title="Teach future transcriptions this spelling">Add to vocabulary</button>'}
          <span class="muted" id="fCount" style="font-size:13px"></span>
        </div>
        <div class="doc ts" id="doc">
          <div class="overview"><div class="lbl">Overview${summary ? '' : ' (add your own notes)'}</div><div class="ov-body" contenteditable="true">${summary || '<p></p>'}</div></div>
          ${sectionsFor().map(secHtml).join('')}
        </div>`;
    }
    $('#body').innerHTML = `<div class="ed-layout"><aside class="card side" id="side"></aside><div>${tabsHtml()}${docHtml}</div></div>`;
    renderSide();
    wireDoc();
    renderPlayer();
  }

  function renderSide() {
    const secs = $$('#doc section.sec');
    const items = secs.length ? secs.map(el => ({ id: el.dataset.id, kind: [...el.classList].find(c => c.startsWith('k-'))?.slice(2) || 'other', title: $('.stitle', el).value, start: +el.dataset.start }))
      : T.sections.map(s => ({ id: s.id, kind: s.kind, title: s.title, start: s.start }));
    $('#side').innerHTML = `<h4>Sections</h4><div class="toc">${items.map(s =>
      `<a href="#" data-goto="${s.id}"><span class="dot k-${esc(s.kind)}"></span><span class="t">${fmtTime(s.start)}</span><span>${esc(s.title)}</span></a>`).join('')}</div>
      ${T.scriptures?.length ? `<h4>Scripture references</h4><div class="scrip">${T.scriptures.map(r => `<a href="#" data-seek="${r.time}">${esc(r.reference)}</a>`).join('')}</div>` : ''}
      <h4>About</h4><p class="muted" style="font-size:13px;margin:0">${esc(T.statusMessage || '')}</p>`;
  }

  function wireTabs() {
    $('#tabs').onclick = async e => {
      const b = e.target.closest('.tab'); if (!b) return;
      if (b.id === 'addTr') return askTranslate();
      if (dirty) await save();
      lang = b.dataset.lang; shell();
    };
  }

  function wireDoc() {
    wireTabs();
    $('#side').onclick = e => {
      const g = e.target.closest('[data-goto]'), s = e.target.closest('[data-seek]');
      if (g) { e.preventDefault(); $('#s-' + g.dataset.goto)?.scrollIntoView({ behavior: 'smooth' }); $('#side').classList.remove('open'); }
      if (s) { e.preventDefault(); seek(+s.dataset.seek); const p = paragraphAt(+s.dataset.seek); p?.scrollIntoView({ behavior: 'smooth', block: 'center' }); }
    };
    $('#trRetry') && ($('#trRetry').onclick = () => startTranslate(lang));
    $('#trDel') && ($('#trDel').onclick = async () => { await api(`transcripts/${id}/translations/${lang}`, { method: 'DELETE' }); delete T.translations[lang]; lang = ''; shell(); });
    const doc = $('#doc'); if (!doc) return;

    doc.addEventListener('input', e => {
      markDirty();
      if (e.target.classList.contains('stitle')) renderSide();
    });
    doc.addEventListener('change', e => {
      if (e.target.classList.contains('kind')) {
        const sec = e.target.closest('section');
        sec.className = sec.className.replace(/k-\S+/, 'k-' + e.target.value);
        markDirty(); renderSide();
      }
    });
    doc.addEventListener('click', e => {
      const op = e.target.closest('[data-op]');
      if (op) return sectionOp(op.dataset.op, op.closest('section'));
      if (e.target.classList.contains('time')) return seek(+e.target.closest('section').dataset.start);
      const p = e.target.closest('.sec-body p[data-t]');
      if (p && doc.classList.contains('ts')) {
        const r = p.getBoundingClientRect();
        const mobile = matchMedia('(max-width:560px)').matches;
        if ((!mobile && e.clientX - r.left < 58) || (mobile && e.clientY - r.top < 22)) seek(+p.dataset.t);
      }
    });
    // Paste as plain text to keep the document clean.
    doc.addEventListener('paste', e => {
      if (!e.target.closest('[contenteditable]')) return;
      e.preventDefault();
      document.execCommand('insertText', false, (e.clipboardData || window.clipboardData).getData('text/plain'));
    });

    $('#tb').addEventListener('mousedown', e => { if (e.target.closest('button[data-cmd]')) e.preventDefault(); });
    $('#tb').onclick = e => {
      const b = e.target.closest('button[data-cmd]'); if (!b) return;
      const c = b.dataset.cmd;
      if (['h2', 'h3', 'p', 'blockquote'].includes(c)) document.execCommand('formatBlock', false, c);
      else if (c === 'mark') toggleMark();
      else document.execCommand(c, false, null);
      markDirty();
    };
    $('#saveBtn').onclick = () => save();
    $('#tsBtn').onclick = () => doc.classList.toggle('ts');
    $('#findBtn').onclick = () => { $('#findbar').classList.toggle('open'); $('#fFind').focus(); };
    $('#fNext').onclick = findNext;
    $('#fFind').onkeydown = e => { if (e.key === 'Enter') findNext(); };
    $('#fAll').onclick = replaceAll;
    $('#fVocab') && ($('#fVocab').onclick = addVocab);
    document.onkeydown = e => { if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); save(); } };
  }

  function toggleMark() {
    const sel = getSelection(); if (!sel.rangeCount || sel.isCollapsed) return;
    const node = sel.anchorNode.nodeType === 3 ? sel.anchorNode.parentElement : sel.anchorNode;
    const existing = node.closest('mark');
    if (existing) { existing.replaceWith(...existing.childNodes); return; }
    const range = sel.getRangeAt(0); const m = document.createElement('mark');
    try { range.surroundContents(m); } catch { document.execCommand('hiliteColor', false, '#fff0a6'); }
  }

  // ---- Section structure editing ----
  function sectionOp(op, sec) {
    if (op === 'del') {
      if (!confirm('Delete this section and its text?')) return;
      const prev = sec.previousElementSibling;
      if (prev?.matches('section.sec')) prev.dataset.end = sec.dataset.end;
      sec.remove();
    } else if (op === 'merge') {
      const prev = sec.previousElementSibling;
      if (!prev?.matches('section.sec')) return toast('This is the first section');
      $('.sec-body', prev).append(...$('.sec-body', sec).childNodes);
      prev.dataset.end = sec.dataset.end;
      sec.remove();
    } else if (op === 'split') {
      const sel = getSelection();
      const p = sel.anchorNode && (sel.anchorNode.nodeType === 3 ? sel.anchorNode.parentElement : sel.anchorNode).closest('.sec-body > *');
      if (!p || !sec.contains(p)) return toast('Click inside the paragraph where the new section should start, then press Split');
      if (!p.previousElementSibling) return toast('That is already the start of the section');
      const start = p.dataset.t ? +p.dataset.t : +sec.dataset.start;
      const ns = { id: Math.random().toString(16).slice(2, 10), kind: [...sec.classList].find(c => c.startsWith('k-')).slice(2), title: 'New section', start, end: +sec.dataset.end, html: '' };
      sec.insertAdjacentHTML('afterend', secHtml(ns));
      const nsEl = sec.nextElementSibling;
      const moving = []; for (let n = p; n; n = n.nextElementSibling) moving.push(n);
      $('.sec-body', nsEl).append(...moving);
      sec.dataset.end = start;
      $('.time', sec).textContent = `${fmtTime(sec.dataset.start)} – ${fmtTime(start)}`;
      $('.stitle', nsEl).select();
    }
    $$('#doc section.sec').forEach(s => $('.time', s).textContent = `${fmtTime(s.dataset.start)} – ${fmtTime(s.dataset.end)}`);
    markDirty(); renderSide();
  }

  // ---- Find & replace ----
  function textNodes() {
    const out = [];
    $$('#doc .sec-body, #doc .ov-body').forEach(root => {
      const w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT); let n; while ((n = w.nextNode())) out.push(n);
    });
    return out;
  }
  function finder() {
    const f = $('#fFind').value; if (!f) return null;
    return new RegExp(f.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), $('#fCase').checked ? 'g' : 'gi');
  }
  let findIdx = 0;
  function findNext() {
    const rx = finder(); if (!rx) return;
    const hits = textNodes().filter(n => { rx.lastIndex = 0; return rx.test(n.nodeValue); });
    const titles = $$('#doc .stitle').filter(i => { rx.lastIndex = 0; return rx.test(i.value); });
    $('#fCount').textContent = `${hits.length + titles.length} found`;
    if (!hits.length) return;
    const n = hits[findIdx++ % hits.length];
    const el = n.parentElement.closest('p,li,h2,h3,blockquote') || n.parentElement;
    el.scrollIntoView({ behavior: 'smooth', block: 'center' });
    el.classList.add('hl-find'); setTimeout(() => el.classList.remove('hl-find'), 1600);
  }
  function replaceAll() {
    const rx = finder(); if (!rx) return;
    const rep = $('#fRep').value; let count = 0;
    textNodes().forEach(n => { const v = n.nodeValue.replace(rx, () => { count++; return rep; }); if (v !== n.nodeValue) n.nodeValue = v; });
    $$('#doc .stitle').forEach(i => { const v = i.value.replace(rx, () => { count++; return rep; }); i.value = v; });
    $('#fCount').textContent = `Replaced ${count}`;
    if (count) { markDirty(); renderSide(); }
  }
  async function addVocab() {
    const word = ($('#fRep').value || $('#fFind').value).trim(); if (!word) return;
    const s = await api('settings');
    const list = s.vocabulary ? s.vocabulary.split(/[,\n]/).map(x => x.trim()).filter(Boolean) : [];
    if (!list.includes(word)) list.push(word);
    s.vocabulary = list.join(', ');
    await api('settings', { method: 'PUT', body: s });
    toast(`"${word}" will be spelled correctly in future transcriptions`);
  }

  // ---- Saving ----
  function markDirty() {
    dirty = true; $('#saveState') && ($('#saveState').textContent = 'Unsaved changes');
    clearTimeout(saveTimer); saveTimer = setTimeout(save, 2500);
  }
  function cleanHtml(el) {
    const c = el.cloneNode(true);
    $$('.playing, .hl-find', c).forEach(x => x.classList.remove('playing', 'hl-find'));
    $$('[class=""]', c).forEach(x => x.removeAttribute('class'));
    return c.innerHTML.trim();
  }
  async function save() {
    clearTimeout(saveTimer);
    if (!dirty || saving || !$('#doc')) return;
    saving = true; $('#saveState').textContent = 'Saving…';
    try {
      const secs = $$('#doc section.sec').map(el => ({
        id: el.dataset.id, title: $('.stitle', el).value, kind: $('.kind', el).value,
        start: +el.dataset.start, end: +el.dataset.end, html: cleanHtml($('.sec-body', el)), summary: el.dataset.summary || null
      }));
      const summaryHtml = cleanHtml($('.ov-body'));
      const summary = summaryHtml.replace(/<[^>]+>/g, '').trim() ? summaryHtml : '';
      if (!lang) {
        await api('transcripts/' + id, { method: 'PUT', body: { title: $('#title').value, summaryHtml: summary, sections: secs } });
        T.title = $('#title').value; T.sections = secs; T.summaryHtml = summary;
      } else {
        const body = { title: $('#title').value, summaryHtml: summary, sections: secs.map(s => ({ id: s.id, title: s.title, html: s.html })) };
        await api(`transcripts/${id}/translations/${lang}`, { method: 'PUT', body });
        Object.assign(T.translations[lang], { title: body.title, summaryHtml: summary, sections: body.sections });
      }
      dirty = false; $('#saveState').textContent = 'Saved ' + new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    } catch (e) { $('#saveState').textContent = 'Save failed'; toast(e.message, true); }
    finally { saving = false; }
  }

  // ---- Translation ----
  function askTranslate() {
    const existing = Object.keys(T.translations || {});
    const opts = Object.entries(STATUS.languages).filter(([c]) => c !== T.detectedLanguage && !existing.includes(c));
    const dlg = document.createElement('div');
    dlg.className = 'card pad'; dlg.style.cssText = 'position:fixed;left:50%;top:20%;transform:translateX(-50%);z-index:60;width:min(420px,92vw)';
    dlg.innerHTML = `<h3 style="margin-top:0;font-family:var(--serif)">Translate this service</h3>
      <label class="field"><span>Language</span><select id="tlang">${opts.map(([c, n]) => `<option value="${c}">${esc(n)}</option>`).join('')}</select></label>
      <p class="muted" style="font-size:13px">Runs on your local AI model (${esc(STATUS.ollama?.active || 'Ollama')}). A full service can take a while - you can keep editing meanwhile.</p>
      <div class="row"><button class="btn primary" id="tgo">Translate</button><button class="btn" id="tcancel">Cancel</button></div>`;
    document.body.append(dlg);
    $('#tcancel', dlg).onclick = () => dlg.remove();
    $('#tgo', dlg).onclick = async () => { dlg.remove(); await startTranslate($('#tlang', dlg).value); };
  }
  async function startTranslate(code) {
    if (dirty) await save();
    try {
      await api(`transcripts/${id}/translate`, { method: 'POST', body: { lang: code } });
      T.translations[code] = { lang: code, status: 'pending', progress: 0 };
      lastTr = trState(); lang = code; shell();
      toast('Translation started');
    } catch (e) { toast(e.message, true); }
  }

  // ---- Audio player ----
  let paras = [];
  function paragraphAt(t) { let best = null; for (const [pt, el] of paras) { if (pt <= t + 0.5) best = el; else break; } return best; }
  function renderPlayer() {
    document.querySelector('.player')?.remove();
    if (!T.hasAudio) return;
    const bar = document.createElement('div');
    bar.className = 'player';
    bar.innerHTML = `<audio controls preload="metadata" src="api/transcripts/${id}/audio"></audio>
      <select class="rate" title="Speed">${[0.75, 1, 1.25, 1.5, 1.75, 2].map(r => `<option ${r === 1 ? 'selected' : ''}>${r}</option>`).join('')}</select>
      <label class="follow"><input type="checkbox" id="follow" checked> Follow</label>`;
    document.body.append(bar);
    const audio = $('audio', bar);
    $('.rate', bar).onchange = e => audio.playbackRate = +e.target.value;
    let current = null;
    audio.addEventListener('timeupdate', () => {
      if (!paras.length) paras = $$('#doc .sec-body p[data-t]').map(p => [+p.dataset.t, p]).sort((a, b) => a[0] - b[0]);
      const p = paragraphAt(audio.currentTime);
      if (p !== current) {
        current?.classList.remove('playing'); p?.classList.add('playing'); current = p;
        if (p && $('#follow').checked && !audio.paused) {
          const r = p.getBoundingClientRect();
          if (r.top < 120 || r.bottom > innerHeight - 90) p.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
      }
    });
    $('#doc')?.addEventListener('input', () => paras = []);
  }
  function seek(t) {
    const a = $('.player audio'); if (!a) return toast('Audio is not available for this service');
    paras = []; a.currentTime = t; a.play().catch(() => {});
  }

  // ---- Menu actions ----
  async function moreAction(act) {
    $$('.menu').forEach(m => m.classList.remove('open'));
    if (act === 'api') { location.hash = `#/api/${id}`; return; }
    try {
      if (act === 'delete') {
        if (!confirm('Delete this service, its transcript, translations and audio?')) return;
        await api('transcripts/' + id, { method: 'DELETE' }); dirty = false; location.hash = '#/'; return;
      }
      if (act === 'resection' && !confirm('Rebuild the sections automatically? Your edits to the text will be replaced.')) return;
      if (act === 'retry' && T.status === 'ready' && !confirm('Download and transcribe again? Your edits will be replaced.')) return;
      await api(`transcripts/${id}/${act}`, { method: 'POST' });
      dirty = false;
      T = await api('transcripts/' + id); lastStatus = T.status; lang = ''; shell();
      toast({ resection: 'Re-organising…', retry: 'Started again', cancel: 'Cancelled' }[act]);
    } catch (e) { toast(e.message, true); }
  }

  document.addEventListener('click', () => $$('.menu').forEach(m => m.classList.remove('open')));
  shell();

  // ---- Live updates while processing / translating ----
  every(async () => {
    const busy = T.status !== 'ready' || Object.values(T.translations || {}).some(x => x.status === 'pending' || x.status === 'running');
    if (!busy) return;
    try {
      const fresh = await api('transcripts/' + id);
      const statusChanged = fresh.status !== lastStatus;
      const prevTr = lastTr;
      T.status = fresh.status; T.progress = fresh.progress; T.statusMessage = fresh.statusMessage; T.error = fresh.error;
      T.translations = fresh.translations; lastTr = trState(); lastStatus = fresh.status;
      if (statusChanged && fresh.status === 'ready') { T = fresh; dirty = false; shell(); return; }
      if (fresh.status !== 'ready') { Object.assign(T, fresh); shell(); return; }
      const cur = lang && T.translations[lang];
      if (cur && cur.status !== 'done') { renderDoc(); return; }
      if (prevTr !== lastTr && lang && T.translations[lang]?.status === 'done' && !dirty) { renderDoc(); return; }
      const tabs = $('#tabs'); if (tabs) { tabs.outerHTML = tabsHtml(); wireTabs(); }
    } catch { /* keep polling */ }
  }, 3000);
}

// ---------------------------------------------------------------- Channel watches
async function viewWatches() {
  view.innerHTML = `
    <h1 class="page">Watch your church's channel</h1>
    <p class="sub">The add-on checks the channel regularly. New and scheduled services are queued automatically and transcribed once the stream has finished.</p>
    <div class="grid2">
      <div id="wlist"></div>
      <div class="card pad">
        <h3 style="margin-top:0;font-family:var(--serif)">Add a channel</h3>
        <label class="field"><span>Name</span><input type="text" id="wName" placeholder="e.g. St Mary's Sunday services"></label>
        <label class="field"><span>Channel or playlist link</span><input type="url" id="wUrl" placeholder="https://www.youtube.com/@yourchurch">
          <small>A channel link, its /streams page, or a playlist.</small></label>
        <label class="check"><input type="checkbox" id="wStreams" checked><span>Use the channel's <b>Live</b> tab (where most churches stream services)</span></label>
        <label class="field"><span>Only titles containing</span><input type="text" id="wFilter" placeholder="e.g. Sunday, Service, Worship">
          <small>Comma-separated, optional. Leave blank to take everything.</small></label>
        <label class="field"><span>Check every</span><select id="wInt"><option value="15">15 minutes</option><option value="30" selected>30 minutes</option><option value="60">hour</option><option value="180">3 hours</option><option value="720">12 hours</option></select></label>
        <label class="field"><span>Spoken language</span><select id="wLang"><option value="">Use default</option>${langOptions('', true)}</select></label>
        <div class="field"><span style="display:block;font-weight:600;margin-bottom:6px;font-size:14px">Translate automatically into</span><div id="wTr">${langChips([])}</div></div>
        <label class="check"><input type="checkbox" id="wLatest"><span>Also transcribe the most recent existing service now</span></label>
        <button class="btn primary" id="wAdd">Start watching</button>
      </div>
    </div>`;
  wireChips($('#wTr'));
  const load = async () => {
    const ws = await api('watches');
    $('#wlist').innerHTML = ws.length ? ws.map(w => `
      <div class="card watch" data-id="${w.id}">
        <div class="row"><h3 class="grow">${esc(w.name)}</h3><span class="pill ${w.enabled ? 'ready' : ''}">${w.enabled ? 'Watching' : 'Paused'}</span></div>
        <dl class="kv">
          <dt>Link</dt><dd><a href="${esc(w.url)}" target="_blank" rel="noopener">${esc(w.url)}</a></dd>
          ${w.titleFilter ? `<dt>Titles with</dt><dd>${esc(w.titleFilter)}</dd>` : ''}
          <dt>Checks</dt><dd>every ${w.intervalMinutes} min · last ${w.lastChecked ? esc(fmtDate(w.lastChecked)) : 'never'}</dd>
          <dt>Queued</dt><dd>${w.queuedCount} service${w.queuedCount === 1 ? '' : 's'}</dd>
          ${w.autoTranslateTo?.length ? `<dt>Translate</dt><dd>${w.autoTranslateTo.map(langName).map(esc).join(', ')}</dd>` : ''}
          ${w.lastError ? `<dt>Problem</dt><dd style="color:var(--bad)">${esc(w.lastError)}</dd>` : ''}
        </dl>
        <div class="row" style="margin-top:12px"><button class="btn small" data-a="check">Check now</button>
          <button class="btn small" data-a="toggle">${w.enabled ? 'Pause' : 'Resume'}</button><button class="btn small danger" data-a="del">Remove</button></div>
      </div>`).join('') : `<div class="card empty"><h2>No channels yet</h2><p>Add your church's YouTube channel and every new service will be transcribed automatically.</p></div>`;
    $('#wlist').onclick = async e => {
      const b = e.target.closest('[data-a]'); if (!b) return;
      const wid = b.closest('.watch').dataset.id; const w = ws.find(x => x.id === wid);
      try {
        if (b.dataset.a === 'check') { b.disabled = true; const r = await api(`watches/${wid}/check`, { method: 'POST' }); toast(r.error ? r.error : r.queued ? `Queued ${r.queued} new service(s)` : 'No new services', !!r.error); }
        if (b.dataset.a === 'toggle') await api('watches/' + wid, { method: 'PUT', body: { enabled: !w.enabled } });
        if (b.dataset.a === 'del' && confirm('Stop watching this channel? Existing transcripts are kept.')) await api('watches/' + wid, { method: 'DELETE' });
      } catch (err) { toast(err.message, true); }
      load();
    };
  };
  $('#wAdd').onclick = async () => {
    const b = $('#wAdd'); b.disabled = true; b.textContent = 'Checking channel…';
    try {
      const r = await api('watches', { method: 'POST', body: {
        name: $('#wName').value, url: $('#wUrl').value, titleFilter: $('#wFilter').value, intervalMinutes: +$('#wInt').value,
        language: $('#wLang').value, translateTo: chosenChips($('#wTr')), useStreamsTab: $('#wStreams').checked, includeLatestExisting: $('#wLatest').checked } });
      toast(r.error ? 'Added, but: ' + r.error : r.queued ? `Watching - queued ${r.queued} service(s)` : 'Watching - new services will be picked up automatically', !!r.error);
      ['#wName', '#wUrl', '#wFilter'].forEach(s => $(s).value = '');
      load();
    } catch (e) { toast(e.message, true); }
    b.disabled = false; b.textContent = 'Start watching';
  };
  await load();
}

// ---------------------------------------------------------------- Settings
async function viewSettings() {
  const [s] = await Promise.all([api('settings'), loadStatus()]);
  const o = STATUS.ollama || {};
  view.innerHTML = `
    <h1 class="page">Settings</h1>
    <p class="sub">Transcription and AI options. Engine settings (Whisper model, Ollama address, password) live on the add-on's Configuration tab in Home Assistant.</p>
    <div class="grid2">
      <div class="card pad">
        <h3 style="margin-top:0;font-family:var(--serif)">Transcription</h3>
        <label class="field"><span>Church name</span><input type="text" id="sChurch" value="${esc(s.churchName)}" placeholder="e.g. Christ Church Cheltenham"></label>
        <label class="field"><span>Vocabulary</span><textarea id="sVocab" rows="4" placeholder="Pastor names, worship leaders, place names, song titles…">${esc(s.vocabulary)}</textarea>
          <small>Names and words here help Whisper spell them correctly. Use "Add to vocabulary" in the editor to grow this list.</small></label>
        <label class="field"><span>Default spoken language</span><select id="sLang">${langOptions(s.defaultLanguage || 'en', true)}</select></label>
        <div class="field"><span style="display:block;font-weight:600;margin-bottom:6px;font-size:14px">Always translate new services into</span><div id="sTr">${langChips(s.autoTranslateTo || [])}</div></div>
        <label class="check"><input type="checkbox" id="sWorship" ${s.omitWorshipLyrics ? 'checked' : ''}><span>Don't transcribe sung worship<br><small class="muted">Worship sections show a short note instead of song lyrics.</small></span></label>
        <label class="check"><input type="checkbox" id="sTs" ${s.exportTimestamps ? 'checked' : ''}><span>Include timestamps in exports</span></label>
        <h3 style="font-family:var(--serif)">Local AI model</h3>
        <label class="field"><span>Ollama model</span>
          <select id="sModel"><option value="">Automatic (prefers a Qwen instruct model)</option>
            ${(o.models || []).map(m => `<option value="${esc(m.name)}" ${m.name === s.ollamaModel ? 'selected' : ''}>${esc(m.name)}${m.size ? ' · ' + esc(m.size) : ''}</option>`).join('')}
          </select><small>Used for sections, summaries, sermon notes and translation.</small></label>
        <button class="btn primary" id="sSave">Save settings</button>
      </div>
      <div class="card pad">
        <h3 style="margin-top:0;font-family:var(--serif)">System status</h3>
        <dl class="kv">
          <dt>Ollama</dt><dd>${o.reachable ? `<span class="pill ready">Connected</span> ${esc(o.url)}` : `<span class="pill failed">Not reachable</span> ${esc(o.url || '')}<br><small style="color:var(--bad)">${esc(o.error || '')}</small>`}</dd>
          <dt>Active model</dt><dd>${esc(o.active || '-')}</dd>
          <dt>AI sections</dt><dd>${STATUS.useAiForSections ? 'On' : 'Off (built-in detector)'}</dd>
          <dt>Whisper</dt><dd>${esc(STATUS.whisper?.model || '')} · ${esc(STATUS.whisper?.compute || '')} (runs inside the add-on)</dd>
          <dt>yt-dlp</dt><dd>${esc(STATUS.ytdlp || '')}</dd>
          <dt>Queue</dt><dd>${STATUS.queue ?? 0} waiting</dd>
          <dt>Save video</dt><dd>${STATUS.downloadVideo ? 'Yes - to the media folder' : 'No (audio only)'}</dd>
          <dt>Version</dt><dd>${esc(STATUS.version || '')}</dd>
        </dl>
        <h4 style="margin-bottom:6px">Open on your phone or computer</h4>
        <p class="muted" style="font-size:14px;margin-top:0">Use the <b>Transcriber</b> item in the Home Assistant sidebar - it works in any browser and in the Home Assistant app on iPhone and Android. In a browser you can also "Add to Home Screen" to use it like an app.</p>
      </div>
    </div>`;
  wireChips($('#sTr'));
  $('#sSave').onclick = async () => {
    try {
      await api('settings', { method: 'PUT', body: {
        churchName: $('#sChurch').value, vocabulary: $('#sVocab').value, defaultLanguage: $('#sLang').value,
        autoTranslateTo: chosenChips($('#sTr')), omitWorshipLyrics: $('#sWorship').checked, exportTimestamps: $('#sTs').checked,
        ollamaModel: $('#sModel').value || null } });
      toast('Settings saved'); loadStatus();
    } catch (e) { toast(e.message, true); }
  };
}

if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').catch(() => {});
route();

// ---------------------------------------------------------------- Automation API
async function viewApi(presetId) {
  const [auto, list] = await Promise.all([api('automation'), api('transcripts')]);
  const ready = list.filter(t => t.status === 'ready');
  const host = auto.internalBaseUrl;
  const PARTS = { summary: 'Overview & sermon notes', scriptures: 'Scripture references', outline: 'Outline (section list)', sections: 'Section text' };
  const st = { id: presetId && ready.some(t => t.id === presetId) ? presetId : 'latest', lang: '', mode: 'whole', parts: new Set(['summary', 'scriptures']),
    secMode: 'all', kinds: new Set(['sermon']), indexes: new Set(), format: 'json', ts: false, max: '' };
  let detail = null;

  view.innerHTML = `
    <h1 class="page">Use transcripts in automations</h1>
    <p class="sub">Pull transcripts into Home Assistant automations (or Node-RED, scripts…) through the API, or let the add-on push them to Home Assistant when a service is ready.</p>
    <div class="grid2">
      <div>
        <div class="card pad" style="margin-bottom:20px">
          <h3 style="margin-top:0;font-family:var(--serif)">1. Choose what to send</h3>
          <label class="field"><span>Service</span><select id="aId"><option value="latest">Latest finished service (always the newest)</option>
            ${ready.map(t => `<option value="${t.id}">${esc(t.title)}${t.uploadDate ? ' · ' + esc(t.uploadDate) : ''}</option>`).join('')}</select></label>
          <label class="field"><span>Language</span><select id="aLang"></select></label>
          <div class="field"><span style="display:block;font-weight:600;margin-bottom:6px;font-size:14px">Content</span>
            <label class="check"><input type="radio" name="aMode" value="whole" checked><span><b>Whole transcript</b> - overview, scriptures, outline and every section</span></label>
            <label class="check"><input type="radio" name="aMode" value="parts"><span><b>Choose parts</b></span></label>
            <div id="aParts" style="margin-left:28px"></div>
          </div>
          <div class="row" style="align-items:flex-end">
            <label class="field grow" style="margin:0"><span>Format</span><select id="aFmt">
              <option value="json">JSON (best for automations)</option><option value="text">Plain text (notifications, TTS)</option>
              <option value="markdown">Markdown</option><option value="html">HTML</option></select></label>
            <label class="field" style="margin:0;width:150px"><span>Max characters</span><input type="number" id="aMax" min="40" placeholder="No limit"></label>
          </div>
          <label class="check" style="margin-top:14px"><input type="checkbox" id="aTs"><span>Include timestamps in the text</span></label>
        </div>
        <div class="card pad">
          <h3 style="margin-top:0;font-family:var(--serif)">3. Push to Home Assistant when a service is ready</h3>
          <label class="check"><input type="checkbox" id="nHa" ${auto.haEvents ? 'checked' : ''}><span>Fire a Home Assistant event<br>
            <small class="muted"><code>${auto.events[0]}</code> when a transcript is ready, <code>${auto.events[1]}</code> for translations, <code>${auto.events[2]}</code> on errors.</small></span></label>
          <label class="field"><span>Webhook (optional)</span><input type="url" id="nHook" value="${esc(auto.webhookUrl)}" placeholder="http://node-red:1880/endpoint">
            <small>Also POST the same JSON to this address - for Node-RED, n8n and similar.</small></label>
          <p style="font-size:14px;margin:0 0 6px"><b>Notifications include:</b> <span id="nSel"></span></p>
          <div class="row"><button class="btn small" id="nUse">Use the selection above for notifications</button>
            <button class="btn small" id="nTest">Send a test now</button><button class="btn small primary" id="nSave">Save</button></div>
          ${auto.runningInHomeAssistant ? '' : '<p class="muted" style="font-size:13px">Events are only available when running as a Home Assistant add-on.</p>'}
        </div>
      </div>
      <div>
        <div class="card pad" style="margin-bottom:20px">
          <h3 style="margin-top:0;font-family:var(--serif)">2. Use it</h3>
          <label class="field"><span>API address (from Home Assistant)</span>
            <div class="row" style="flex-wrap:nowrap"><input type="text" id="aUrl" readonly style="font-family:ui-monospace,monospace;font-size:13px;min-width:0"><button class="btn small" data-copy="aUrl">Copy</button></div></label>
          <label class="field"><span>API key</span>
            <div class="row"><input type="password" id="aKey" readonly value="${esc(auto.apiKey)}" style="font-family:ui-monospace,monospace;font-size:13px;flex:1 1 180px;min-width:0;width:auto">
              <button class="btn small" id="aShow">Show</button><button class="btn small" data-copy="aKey">Copy</button><button class="btn small danger" id="aRegen">New key</button></div>
            <small>Send it as the header <code>Authorization: Bearer &lt;key&gt;</code>. Keep it in <code>secrets.yaml</code>.</small></label>
          <div class="row"><button class="btn primary" id="aTry">Preview result</button><span class="muted" id="aInfo" style="font-size:13px"></span></div>
          <pre id="aOut" class="codebox" style="display:none"></pre>
        </div>
        <div class="card pad">
          <h3 style="margin-top:0;font-family:var(--serif)">Home Assistant examples</h3>
          <div class="tabs" id="yTabs"><button class="tab on" data-y="event">Event automation</button><button class="tab" data-y="rest">Pull on demand</button><button class="tab" data-y="sensor">Sensor</button></div>
          <p class="muted" id="yHelp" style="font-size:13px"></p>
          <pre id="yOut" class="codebox"></pre>
          <button class="btn small" data-copy="yOut">Copy YAML</button>
        </div>
      </div>
    </div>`;

  const query = () => {
    const q = [];
    if (st.mode === 'parts') {
      const parts = Object.keys(PARTS).filter(p => st.parts.has(p));
      if (parts.length) q.push('parts=' + parts.join(','));
      if (st.parts.has('sections') && st.secMode === 'kinds' && st.kinds.size) q.push('kinds=' + [...st.kinds].join(','));
      if (st.parts.has('sections') && st.secMode === 'pick' && st.indexes.size) q.push('sections=' + [...st.indexes].sort((a, b) => a - b).join(','));
    }
    if (st.format !== 'json') q.push('format=' + st.format);
    if (st.lang) q.push('lang=' + st.lang);
    if (st.ts) q.push('timestamps=1');
    if (+st.max > 0) q.push('max_chars=' + (+st.max));
    return q.join('&');
  };
  const path = () => `api/v1/transcripts/${st.id}` + (query() ? '?' + query() : '');
  const describe = qs => {
    const p = new URLSearchParams(qs); const parts = p.get('parts');
    const what = parts ? parts.split(',').map(x => PARTS[x] || x).join(', ') : 'the whole transcript';
    const extra = [p.get('kinds') && `only ${p.get('kinds').split(',').map(k => KINDS[k] || k).join(', ')} sections`, p.get('sections') && `sections ${p.get('sections')}`].filter(Boolean);
    return what + (extra.length ? ` (${extra.join('; ')})` : '');
  };

  async function loadDetail() {
    detail = st.id === 'latest' ? (ready[0] ? await api('transcripts/' + ready[0].id) : null) : await api('transcripts/' + st.id);
    const langs = detail ? Object.values(detail.translations || {}).filter(x => x.status === 'done').map(x => x.lang) : [];
    const all = st.id === 'latest' ? Object.keys(STATUS.languages) : langs;
    $('#aLang').innerHTML = `<option value="">Original${detail?.detectedLanguage ? ' (' + esc(langName(detail.detectedLanguage)) + ')' : ''}</option>` +
      all.filter(c => c !== detail?.detectedLanguage).map(c => `<option value="${c}" ${c === st.lang ? 'selected' : ''}>${esc(langName(c))}${langs.includes(c) ? '' : ' (if translated)'}</option>`).join('');
    if (!all.includes(st.lang)) st.lang = '';
    drawParts(); update();
  }

  function drawParts() {
    const box = $('#aParts');
    if (st.mode !== 'parts') { box.innerHTML = ''; return; }
    const secs = detail?.sections || [];
    box.innerHTML = Object.entries(PARTS).map(([k, n]) => `<label class="check" style="margin-bottom:8px"><input type="checkbox" data-part="${k}" ${st.parts.has(k) ? 'checked' : ''}><span>${n}</span></label>`).join('') +
      (st.parts.has('sections') ? `<div style="margin-left:28px">
        <label class="check" style="margin-bottom:6px"><input type="radio" name="secMode" value="all" ${st.secMode === 'all' ? 'checked' : ''}><span>All sections</span></label>
        <label class="check" style="margin-bottom:6px"><input type="radio" name="secMode" value="kinds" ${st.secMode === 'kinds' ? 'checked' : ''}><span>Only these kinds of section <small class="muted">(works for any service)</small></span></label>
        ${st.secMode === 'kinds' ? `<div class="chips" id="aKinds" style="margin:0 0 10px 28px">${Object.entries(KINDS).map(([k, n]) => `<span class="chip ${st.kinds.has(k) ? 'on' : ''}" data-kind="${k}">${esc(n)}</span>`).join('')}</div>` : ''}
        ${st.id !== 'latest' ? `<label class="check" style="margin-bottom:6px"><input type="radio" name="secMode" value="pick" ${st.secMode === 'pick' ? 'checked' : ''}><span>Only these sections of this service</span></label>
        ${st.secMode === 'pick' ? `<div style="margin-left:28px">${secs.map((x, i) => `<label class="check" style="margin-bottom:4px"><input type="checkbox" data-idx="${i + 1}" ${st.indexes.has(i + 1) ? 'checked' : ''}><span>${i + 1}. ${esc(x.title)} <small class="muted">${fmtTime(x.start)}</small></span></label>`).join('')}</div>` : ''}` : ''}
      </div>` : '');
  }

  function update() {
    $('#aUrl').value = `${host}/${path()}`;
    $('#nSel').textContent = describe(auto.notifySelection);
    yaml();
  }

  let yTab = 'event';
  function yaml() {
    const url = `${host}/${path()}`;
    const fmtJson = st.format === 'json';
    const msg = fmtJson ? (st.mode === 'parts' && !st.parts.has('summary') && !st.parts.has('sections') ? "{{ transcript.content.title }}" : "{{ transcript.content.text[:1000] }}") : "{{ transcript.content[:1000] }}";
    const help = {
      event: `Runs when a service finishes. The event carries what you set under step 3 (now: ${describe(auto.notifySelection)}). Change the notify service to your phone.`,
      rest: 'Add the rest_command to configuration.yaml and the key to secrets.yaml, restart, then call it from any script or automation and use the response.',
      sensor: 'A sensor whose state is the latest service title, with the chosen parts as attributes (the state itself is limited to 255 characters).'
    }[yTab];
    const secret = `# secrets.yaml\nchurch_transcriber_auth: "Bearer ${auto.apiKey}"\n\n`;
    const out = {
      event: `alias: Sermon notes when a service is ready
triggers:
  - trigger: event
    event_type: ${auto.events[0]}
actions:
  - action: notify.notify
    data:
      title: "{{ trigger.event.data.title }}"
      message: >-
        {{ (trigger.event.data.summary.text if trigger.event.data.summary else trigger.event.data.text)[:1000] }}
      data:
        url: "{{ trigger.event.data.url }}"`,
      rest: `${secret}# configuration.yaml
rest_command:
  church_transcript:
    url: "${url}"
    method: GET
    headers:
      Authorization: !secret church_transcriber_auth
    timeout: 60

# In a script or automation:
actions:
  - action: rest_command.church_transcript
    response_variable: transcript
  - action: notify.notify
    data:
      title: ${fmtJson ? '"{{ transcript.content.title }}"' : 'Latest service'}
      message: "${msg}"`,
      sensor: `${secret}# configuration.yaml
rest:
  - resource: "${host}/api/v1/transcripts/${st.id}${(() => { const q = query().replace(/(^|&)format=[^&]*/, '').replace(/^&/, ''); return q ? '?' + q : ''; })()}"
    headers:
      Authorization: !secret church_transcriber_auth
    scan_interval: 1800
    sensor:
      - name: Latest church service
        value_template: "{{ value_json.title }}"
        json_attributes: [date, url, summary, scriptures, outline, sections, text]`
    }[yTab];
    $('#yHelp').textContent = help; $('#yOut').textContent = out;
  }

  // ---- wiring ----
  $('#aId').value = st.id;
  $('#aId').onchange = e => { st.id = e.target.value; st.indexes.clear(); if (st.secMode === 'pick' && st.id === 'latest') st.secMode = 'all'; loadDetail(); };
  $('#aLang').onchange = e => { st.lang = e.target.value; update(); };
  $$('input[name=aMode]').forEach(r => r.onchange = e => { st.mode = e.target.value; drawParts(); update(); });
  $('#aParts').addEventListener('change', e => {
    const t = e.target;
    if (t.dataset.part) { t.checked ? st.parts.add(t.dataset.part) : st.parts.delete(t.dataset.part); drawParts(); }
    if (t.name === 'secMode') { st.secMode = t.value; drawParts(); }
    if (t.dataset.idx) { t.checked ? st.indexes.add(+t.dataset.idx) : st.indexes.delete(+t.dataset.idx); }
    update();
  });
  $('#aParts').addEventListener('click', e => { const c = e.target.closest('[data-kind]'); if (!c) return; c.classList.toggle('on'); c.classList.contains('on') ? st.kinds.add(c.dataset.kind) : st.kinds.delete(c.dataset.kind); update(); });
  $('#aFmt').onchange = e => { st.format = e.target.value; update(); };
  $('#aMax').oninput = e => { st.max = e.target.value; update(); };
  $('#aTs').onchange = e => { st.ts = e.target.checked; update(); };
  $('#aShow').onclick = () => { const k = $('#aKey'); k.type = k.type === 'password' ? 'text' : 'password'; $('#aShow').textContent = k.type === 'password' ? 'Show' : 'Hide'; };
  $('#aRegen').onclick = async () => {
    if (!confirm('Create a new API key? Automations using the old key will stop working until you update them.')) return;
    const r = await api('automation/regenerate-key', { method: 'POST' }); auto.apiKey = r.apiKey; $('#aKey').value = r.apiKey; update(); toast('New API key created');
  };
  view.addEventListener('click', async e => {
    const b = e.target.closest('[data-copy]'); if (!b) return;
    const el = $('#' + b.dataset.copy); const text = el.value ?? el.textContent;
    try { await navigator.clipboard.writeText(text); } catch { const ta = document.createElement('textarea'); ta.value = text; document.body.append(ta); ta.select(); document.execCommand('copy'); ta.remove(); }
    toast('Copied');
  });
  $('#yTabs').onclick = e => { const b = e.target.closest('.tab'); if (!b) return; yTab = b.dataset.y; $$('#yTabs .tab').forEach(x => x.classList.toggle('on', x === b)); yaml(); };
  $('#aTry').onclick = async () => {
    const out = $('#aOut'); out.style.display = 'block'; out.textContent = 'Loading…';
    try {
      const res = await fetch(path()); let body = await res.text();
      if (st.format === 'json') { try { body = JSON.stringify(JSON.parse(body), null, 2); } catch { } }
      $('#aInfo').textContent = `${res.status === 200 ? 'OK' : 'HTTP ' + res.status} · ${body.length.toLocaleString()} characters`;
      out.textContent = body.length > 6000 ? body.slice(0, 6000) + '\n… (preview shortened)' : body;
    } catch (err) { out.textContent = err.message; }
  };
  $('#nUse').onclick = () => {
    const q = query().split('&').filter(x => x && !/^(format|lang)=/.test(x)).join('&');
    auto.notifySelection = q; update(); toast('Notifications will use this selection - press Save');
  };
  $('#nSave').onclick = async () => {
    try {
      const r = await api('automation', { method: 'PUT', body: { haEvents: $('#nHa').checked, webhookUrl: $('#nHook').value, notifySelection: auto.notifySelection } });
      auto.notifySelection = r.notifySelection; update(); toast('Notification settings saved');
    } catch (e) { toast(e.message, true); }
  };
  $('#nTest').onclick = async () => {
    try { const r = await api('automation/test', { method: 'POST' }); toast(`Sent "${r.sent}"`); } catch (e) { toast(e.message, true); }
  };
  await loadDetail();
}
