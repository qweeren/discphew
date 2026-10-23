// The page: find the release, choose and place the artwork for each part, make the PDF.
// A port of discphew's Cover Printer (src/UI/CoverView.cs) for phones and browsers.

import * as mb from './musicbrainz.js';
import { albumFromFiles, toc, discId } from './flac.js';
import { render, bookletPages, bookletSide, bookletFront, impose, withBleed, newPlacement, isDefault, BLEED, SPINE, DISC_OUTER, DISC_HOLE, PRINT_SCALE } from './render.js';
import { layout, jpeg } from './pdf.js';

const $ = id => document.getElementById(id);
const store = {
  get(key, fallback) { try { return localStorage.getItem('covers.' + key) ?? fallback; } catch { return fallback; } },
  set(key, value) { try { localStorage.setItem('covers.' + key, value); } catch { /* private browsing */ } },
};

const GENERATE = { id: 'generate', label: 'Generate from the track list', generate: true };
const PARTS = ['front', 'tray', 'disc'];

const S = {
  source: null,             // the album from the user's files, if they opened any
  release: null,            // the release the artwork comes from
  album: null,              // what gets printed: names, year, tracks
  typed: { artist: null, title: null },
  choices: [],              // { id, label, remote | blob | generate, image, loading, failed, thumb }
  booklet: [],
  chosen: {},               // part → choice
  picked: new Set(),        // parts whose art the user chose, not to be overridden
  places: { front: newPlacement(), tray: newPlacement(), disc: newPlacement() },
  part: 'front',
  gen: 0,                   // bumped when the release changes, so late downloads are dropped
  itunesAsked: false,
  pdfUrl: null,
};

// ------------------------------------------------------------------ finding the release

function status(el, text, error = false) {
  el.textContent = text;
  el.classList.toggle('error', error);
}

const findStatus = t => status($('find-status'), t);
const findError = e => status($('find-status'), e.message || String(e), true);

$('search-form').addEventListener('submit', async e => {
  e.preventDefault();
  const artist = $('q-artist').value, album = $('q-album').value;
  if (!album.trim()) { status($('find-status'), 'Type the album title (and the artist, to narrow it down).', true); return; }
  findStatus('Searching MusicBrainz…');
  try { showResults(await mb.search(artist, album, S.source?.tracks.length || 0), 'search'); } catch (err) { findError(err); }
});

$('id-form').addEventListener('submit', async e => {
  e.preventDefault();
  const id = mb.parseId($('q-id').value);
  if (!id) { status($('find-status'), 'That isn\'t a MusicBrainz release link, release ID or disc ID.', true); return; }
  await lookup(id);
});

async function lookup(id) {
  findStatus('Looking it up on MusicBrainz…');
  try {
    if (id.release) {
      const r = await mb.release(id.release, S.source?.disc || 0, S.source?.tracks.length || 0);
      showResults(r ? [r] : [], 'its release ID');
    } else showResults(await mb.byDiscId(id.discId), 'its disc ID');
  } catch (err) { findError(err); }
}

$('files').addEventListener('change', async e => {
  const files = [...e.target.files];
  e.target.value = '';
  if (!files.length) return;
  findStatus(`Reading ${files.length} file${files.length === 1 ? '' : 's'}…`);
  try {
    const a = await albumFromFiles(files);
    // A new album: its artwork starts over.
    S.source = a;
    S.gen++;
    S.choices = [];
    S.chosen = {};
    S.picked.clear();
    S.typed = { artist: null, title: null };
    for (const p of PARTS) S.places[p] = newPlacement();
    $('q-artist').value = a.artist;
    $('q-album').value = a.title;
    // The exact release from the tags, else the exact pressing from the disc ID, else a search on the names.
    if (a.releaseId) {
      findStatus('Looking up the release the files are tagged with…');
      const r = await mb.release(a.releaseId, a.disc, a.tracks.length);
      if (r) return showResults([r], 'the release they\'re tagged with');
    }
    const t = toc(a.sectors);
    findStatus('Looking up the disc ID…');
    const byDisc = await mb.byDiscId(await discId(t), t.join('+'));
    if (byDisc.length) return showResults(byDisc, 'their disc ID');
    findStatus('Searching MusicBrainz…');
    showResults(await mb.search(a.artist, a.title, a.tracks.length), 'search');
  } catch (err) { findError(err); }
});

function showResults(list, how) {
  const ul = $('results');
  ul.replaceChildren();
  if (!list.length) {
    status($('find-status'), how === 'search' ? 'Nothing on MusicBrainz by that name. Check the spelling.' : 'Not found on MusicBrainz.', true);
    return;
  }
  findStatus(how === 'search' ? `${list.length} release${list.length === 1 ? '' : 's'}: pick the one that matches your copy` : `Found by ${how}` + (list.length > 1 ? ': pick your pressing' : ''));
  for (const r of list) {
    const li = document.createElement('li');
    const b = document.createElement('button');
    b.type = 'button';
    const img = document.createElement('img');
    img.alt = '';
    img.loading = 'lazy';
    img.src = `https://coverartarchive.org/release/${r.id}/front-250`;
    img.onerror = () => { const n = document.createElement('span'); n.className = 'noimg'; img.replaceWith(n); };
    const text = document.createElement('span');
    const t = document.createElement('span'); t.className = 't'; t.textContent = `${r.artist} – ${r.title}`;
    const d = document.createElement('span'); d.className = 'd';
    d.textContent = [r.date, r.country, r.mediumCount > 1 && `disc ${r.mediumPosition}/${r.mediumCount}`, r.format && (r.trackCount ? `${r.format}, ${r.trackCount} tracks` : r.format)].filter(Boolean).join('  ·  ');
    text.append(t, d);
    b.append(img, text);
    b.addEventListener('click', () => useRelease(r, b));
    li.append(b);
    ul.append(li);
  }
  if (how !== 'search') useRelease(list[0], ul.querySelector('button'));
}

/** Fills the album in from the release (the files' own names and titles win) and lists its artwork. */
async function useRelease(r, button) {
  const gen = ++S.gen;
  for (const b of $('results').querySelectorAll('button')) b.removeAttribute('aria-current');
  button?.setAttribute('aria-current', 'true');
  findStatus('Fetching the release and its artwork…');
  try {
    // Search results come without their track lists.
    const full = r.tracks.length ? r : (await mb.release(r.id, S.source?.disc || 0, S.source?.tracks.length || 0)) || r;
    const images = await mb.coverArt(full);
    if (gen !== S.gen) return;
    S.release = full;
    S.album = fill(S.source, full);
    if (S.typed.artist?.trim()) S.album.artist = S.typed.artist;
    if (S.typed.title?.trim()) S.album.title = S.typed.title;

    // Scans of the last release go; the user's own images stay.
    S.choices = S.choices.filter(c => !c.remote);
    for (const p of PARTS) if (S.chosen[p]?.remote) { delete S.chosen[p]; S.picked.delete(p); }
    if (S.source?.cover && !S.choices.some(c => c.own)) {
      const own = { id: 'own', label: 'Front · from the files', blob: S.source.cover, own: true };
      try { own.image = await mb.decode(own.blob); S.choices.push(own); } catch { /* not an image after all */ }
    }
    S.booklet = [];
    S.itunesAsked = false;
    for (const img of images) {
      const c = { id: img.id, label: mb.label(img), remote: img };
      if (mb.is(img, 'Booklet') && !mb.is(img, 'Front')) S.booklet.push(c);
      S.choices.push(c);
    }
    const first = type => S.choices.find(c => c.remote && mb.is(c.remote, type));
    pick('front', first('Front') || (S.chosen.front ? null : S.choices.find(c => c.own)));
    if (!S.chosen.tray) S.chosen.tray = GENERATE;
    pick('tray', first('Back'));
    pick('disc', first('Medium'));
    checkFront();

    const n = images.length;
    findStatus(n ? `${n} image${n === 1 ? '' : 's'} on the Cover Art Archive` : 'The Cover Art Archive has no artwork for this release: upload your own, or print the generated back inlay.');
    $('release-line').textContent = mb.describe(full);
    $('artist').value = S.album.artist;
    $('title').value = S.album.title;
    $('design').hidden = $('print').hidden = false;
    history.replaceState(null, '', '#release=' + full.id);
    refresh();
  } catch (err) { if (gen === S.gen) findError(err); }
}

function fill(source, r) {
  const tracks = source?.tracks.length
    ? source.tracks.map((t, i) => ({
      number: t.number,
      title: t.title || r.tracks[i]?.title || '',
      artist: t.artist || r.tracks[i]?.artist || '',
      length: t.length || r.tracks[i]?.length || 0,
    }))
    : r.tracks.map(t => ({ ...t }));
  return { artist: source?.artist || r.artist, title: source?.title || r.title, year: source?.year || r.year, tracks };
}

// ------------------------------------------------------------------ choosing artwork

/** Makes c the art for a part, unless the user chose that part's art themselves. The disc label follows the front. */
function pick(part, c) {
  if (!c || S.picked.has(part)) return;
  const old = S.chosen[part];
  S.chosen[part] = c;
  if (part === 'front' && !S.picked.has('disc') && (!S.chosen.disc || S.chosen.disc === old || !S.chosen.disc.remote || !mb.is(S.chosen.disc.remote, 'Medium')))
    S.chosen.disc = c;
  load(c);
}

/** The user's choice: kept over any default, and the placement starts over. */
function choose(part, c) {
  const old = S.chosen[part];
  S.chosen[part] = c;
  S.picked.add(part);
  S.places[part] = newPlacement();
  if (part === 'front' && !S.picked.has('disc') && S.chosen.disc === old) { S.chosen.disc = c; S.places.disc = newPlacement(); }
  load(c);
  refresh();
}

/** Too small to print sharp: 120 mm at 300 DPI wants 1400-odd pixels, and under 1000 it starts to show. */
const small = c => !c || c.failed || (c.image && c.image.naturalWidth < 1000);

function checkFront() { if (small(S.chosen.front)) askItunes(); }

/** The iTunes front, usually 3000 px; it becomes the front unless the user picked one. */
async function askItunes() {
  if (S.itunesAsked || !S.album) return;
  S.itunesAsked = true;
  const gen = S.gen;
  const upcs = await mb.barcodes(S.release).catch(() => []);
  const img = await mb.itunes(S.album.artist, S.album.title, upcs);
  if (!img || gen !== S.gen) return;
  const c = { id: img.id, label: 'Front · iTunes', remote: img };
  S.choices.push(c);
  if (small(S.chosen.front)) pick('front', c);
  refresh();
}

/** Downloads a preview-quality copy (1200 px) of a scan. */
async function load(c) {
  if (!c || c.image || c.loading || !c.remote) return;
  c.loading = true;
  c.failed = false;
  const gen = S.gen;
  try { c.image = await mb.loadImage(mb.url(c.remote, 1200)); }
  catch { c.failed = true; }
  c.loading = false;
  if (gen !== S.gen) return;
  if (S.chosen.front === c) checkFront();
  refresh();
}

const artFor = part => S.chosen[part] && !S.chosen[part].generate ? S.chosen[part].image || null : null;
const adjustable = () => S.part !== 'booklet' && S.chosen[S.part] && !S.chosen[S.part].generate;

function renderChoices() {
  const box = $('choices');
  box.replaceChildren();
  box.hidden = S.part === 'booklet';
  if (box.hidden) return;
  const list = (S.part === 'tray' ? [GENERATE] : []).concat(S.choices);
  for (const c of list) {
    const b = document.createElement('button');
    b.type = 'button';
    b.setAttribute('aria-pressed', String(S.chosen[S.part] === c));
    const thumb = document.createElement(c.generate ? 'span' : 'img');
    thumb.className = 'thumb';
    if (c.generate) thumb.textContent = '≡';
    else {
      thumb.alt = '';
      thumb.loading = 'lazy';
      thumb.src = c.remote ? mb.url(c.remote, 250) : (c.thumb ||= URL.createObjectURL(c.blob));
    }
    const text = document.createElement('span');
    text.textContent = c.label + (c.loading ? ' (loading…)' : c.failed ? ' (unavailable)' : c.image ? ` · ${c.image.naturalWidth} px` : '');
    b.append(thumb, text);
    b.addEventListener('click', () => choose(S.part, c));
    box.append(b);
  }
  const up = document.createElement('label');
  up.className = 'button file upload';
  up.innerHTML = '<span class="thumb">+</span><span>Upload an image</span>';
  const input = document.createElement('input');
  input.type = 'file';
  input.accept = 'image/*';
  input.addEventListener('change', async () => {
    const f = input.files[0];
    if (!f) return;
    try {
      const c = { id: 'file-' + Date.now(), label: f.name, blob: f, image: await mb.decode(f) };
      S.choices.push(c);
      choose(S.part, c);
    } catch (err) { status($('print-status'), err.message, true); }
  });
  up.append(input);
  box.append(up);
}

function refresh() {
  for (const t of document.querySelectorAll('.tabs button')) t.setAttribute('aria-selected', String(t.dataset.part === S.part));
  renderChoices();
  const adj = adjustable();
  $('tools').hidden = !adj;
  $('reset').disabled = !adj || isDefault(S.places[S.part]);
  $('preview').classList.toggle('static', !adj);
  $('hint').textContent = adj ? 'Drag to move · pinch or scroll to zoom · ↺ ↻ to straighten a crooked scan'
    : S.part === 'tray' ? 'Made from the track list over a blur of the front cover. Pick a back scan above to use that instead.'
    : S.part === 'booklet' ? (S.booklet.length ? 'Each sheet prints double-sided and folds in the middle. Nest the sheets in order and staple at the fold.' : 'The Cover Art Archive has no booklet scans for this release.')
    : 'Pick an image above.';
  if (S.part === 'booklet') S.booklet.forEach(load);
  const n = S.booklet.length;
  $('opt-booklet').disabled = !n;
  if (!n) $('opt-booklet').checked = false;
  $('booklet-label').textContent = n ? `Full booklet (${n} scan${n === 1 ? '' : 's'})` : 'Full booklet';
  schedule();
}

for (const t of document.querySelectorAll('.tabs button')) t.addEventListener('click', () => { S.part = t.dataset.part; refresh(); });

$('artist').addEventListener('input', e => { if (S.album) { S.album.artist = S.typed.artist = e.target.value; schedule(); } });
$('title').addEventListener('input', e => { if (S.album) { S.album.title = S.typed.title = e.target.value; schedule(); } });

// ------------------------------------------------------------------ preview

const preview = $('preview');
let frame = 0, cssScale = 1;

function schedule() { if (!frame) frame = requestAnimationFrame(() => { frame = 0; draw(); }); }

function draw() {
  if (!S.album || $('design').hidden) return;
  const stage = $('stage');
  const width = stage.clientWidth - 24;
  const dpr = Math.min(window.devicePixelRatio || 1, S.part === 'booklet' ? 2 : 3);
  const g = preview.getContext('2d');
  if (S.part === 'booklet') {
    // Every printed side in print order, one sheet (outside, inside) to a row on wide screens.
    const cols = width >= 640 ? 2 : 1, gap = 8;
    cssScale = width / (cols * 246 + (cols - 1) * gap);
    const k = cssScale * dpr;
    const front = render('front', artFor('front'), S.places.front, S.album, null, k);
    const pages = bookletPages(bookletFront(front), S.booklet.filter(c => c.image).map(c => c.image));
    const sides = impose(pages.length);
    const rows = Math.ceil(sides.length / cols);
    preview.width = Math.round(width * dpr);
    preview.height = Math.round((rows * 126 + (rows - 1) * gap) * k);
    preview.style.width = width + 'px';
    preview.style.height = preview.height / dpr + 'px';
    sides.forEach(([l, r], i) => {
      const side = bookletSide(l < 0 ? null : pages[l], r < 0 ? null : pages[r], k);
      g.drawImage(side, (i % cols) * (246 + gap) * k, Math.floor(i / cols) * (126 + gap) * k);
      side.width = side.height = 0;
    });
    front.width = front.height = 0;
    return;
  }
  const [mmW, mmH] = withBleed(S.part);
  cssScale = Math.min(width / mmW, (window.innerHeight * (window.innerWidth >= 760 ? 0.62 : 0.55)) / mmH);
  const k = cssScale * dpr;
  const art = render(S.part, artFor(S.part), S.places[S.part], S.album, artFor('front'), k);
  preview.width = art.width;
  preview.height = art.height;
  preview.style.width = art.width / dpr + 'px';
  preview.style.height = art.height / dpr + 'px';
  g.drawImage(art, 0, 0);
  art.width = art.height = 0;
  guides(g, S.part, k);
}

/** Shades the bleed (it's cut off), outlines the cut and dashes the folds. */
function guides(g, part, k) {
  const W = preview.width, H = preview.height, b = BLEED * k;
  g.save();
  g.fillStyle = 'rgba(28,30,35,0.66)';
  g.strokeStyle = 'rgba(108,200,240,0.85)';
  g.lineWidth = Math.max(1, k / 4);
  g.beginPath();
  g.rect(0, 0, W, H);
  if (part === 'disc') {
    g.arc(W / 2, H / 2, DISC_OUTER / 2 * k, 0, Math.PI * 2);
    g.fill('evenodd');
    g.beginPath();
    g.arc(W / 2, H / 2, DISC_HOLE / 2 * k, 0, Math.PI * 2);
    g.fill();
    for (const r of [DISC_OUTER / 2, DISC_HOLE / 2]) { g.beginPath(); g.arc(W / 2, H / 2, r * k, 0, Math.PI * 2); g.stroke(); }
  } else {
    g.rect(b, b, W - 2 * b, H - 2 * b);
    g.fill('evenodd');
    g.strokeRect(b, b, W - 2 * b, H - 2 * b);
    if (part === 'tray') {
      g.setLineDash([4 * g.lineWidth, 3 * g.lineWidth]);
      for (const x of [b + SPINE * k, W - b - SPINE * k]) { g.beginPath(); g.moveTo(x, b); g.lineTo(x, H - b); g.stroke(); }
    }
  }
  g.restore();
}

window.addEventListener('resize', schedule);

// One finger (or the mouse) drags the art; two pinch to zoom and drag together.
const pointers = new Map();
let last = null;

function gesture() {
  const pts = [...pointers.values()];
  const cx = pts.reduce((s, p) => s + p.x, 0) / pts.length, cy = pts.reduce((s, p) => s + p.y, 0) / pts.length;
  const spread = pts.length > 1 ? Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y) : 0;
  return { n: pts.length, cx, cy, spread };
}

preview.addEventListener('pointerdown', e => {
  if (!adjustable()) return;
  preview.focus({ preventScroll: true });
  preview.setPointerCapture(e.pointerId);
  pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
  last = gesture();
});

preview.addEventListener('pointermove', e => {
  if (!pointers.has(e.pointerId)) return;
  pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
  const now = gesture(), p = S.places[S.part];
  if (last && now.n === last.n) {
    p.x += (now.cx - last.cx) / cssScale;
    p.y += (now.cy - last.cy) / cssScale;
    if (now.n > 1 && last.spread > 0) p.zoom = Math.max(0.2, Math.min(8, p.zoom * now.spread / last.spread));
    moved();
  }
  last = now;
});

for (const type of ['pointerup', 'pointercancel']) preview.addEventListener(type, e => { pointers.delete(e.pointerId); last = pointers.size ? gesture() : null; });

preview.addEventListener('wheel', e => {
  if (!adjustable()) return;
  e.preventDefault();
  zoom(Math.pow(1.05, -e.deltaY / 100));
}, { passive: false });

preview.addEventListener('dblclick', () => { if (adjustable()) { S.places[S.part] = newPlacement(); moved(); } });

preview.addEventListener('keydown', e => {
  if (!adjustable()) return;
  const p = S.places[S.part], step = e.shiftKey ? 0.1 : e.ctrlKey ? 2 : 0.5;
  const keys = {
    ArrowLeft: () => p.x -= step, ArrowRight: () => p.x += step, ArrowUp: () => p.y -= step, ArrowDown: () => p.y += step,
    '[': () => turn(e.shiftKey ? -1 : -0.25), ']': () => turn(e.shiftKey ? 1 : 0.25), '+': () => zoom(1.02), '=': () => zoom(1.02), '-': () => zoom(1 / 1.02),
    0: () => { S.places[S.part] = newPlacement(); },
  };
  if (!keys[e.key]) return;
  e.preventDefault();
  keys[e.key]();
  moved();
});

function moved() { $('reset').disabled = isDefault(S.places[S.part]); schedule(); }
function zoom(f) { const p = S.places[S.part]; p.zoom = Math.max(0.2, Math.min(8, p.zoom * f)); moved(); }
function turn(deg) { const p = S.places[S.part]; p.angle = Math.round((p.angle + deg) * 100) / 100; moved(); }

$('turn-left').addEventListener('click', () => turn(-0.5));
$('turn-right').addEventListener('click', () => turn(0.5));
$('zoom-in').addEventListener('click', () => zoom(1.05));
$('zoom-out').addEventListener('click', () => zoom(1 / 1.05));
$('reset').addEventListener('click', () => { S.places[S.part] = newPlacement(); moved(); });

// ------------------------------------------------------------------ the PDF

$('paper').value = store.get('paper', navigator.language === 'en-US' ? 'letter' : 'a4');
$('opt-disc').checked = store.get('disc', '1') === '1';
$('opt-original').checked = store.get('original', matchMedia('(pointer: coarse)').matches ? '0' : '1') === '1';
$('paper').addEventListener('change', e => store.set('paper', e.target.value));
$('opt-disc').addEventListener('change', e => store.set('disc', e.target.checked ? '1' : '0'));
$('opt-original').addEventListener('change', e => store.set('original', e.target.checked ? '1' : '0'));
$('opt-booklet').addEventListener('change', e => { if (e.target.checked) { S.part = 'booklet'; refresh(); } });

$('make').addEventListener('click', makePdf);

async function makePdf() {
  if (!S.album) return;
  const button = $('make'), bar = $('progress'), say = t => status($('print-status'), t);
  const withDisc = $('opt-disc').checked, withBooklet = $('opt-booklet').checked && S.booklet.length > 0, originals = $('opt-original').checked;
  const letter = $('paper').value === 'letter';
  button.disabled = true;
  $('download').hidden = true;
  bar.hidden = false;
  const scans = withBooklet ? S.booklet : [];
  let done = 0;
  const total = 3 + scans.length;
  const step = text => { say(text); bar.value = ++done / total; };
  const album = { ...S.album, tracks: S.album.tracks.map(t => ({ ...t })) };
  const places = Object.fromEntries(PARTS.map(p => [p, { ...S.places[p] }]));

  /** The full-size original for print when asked for (falling back to the preview copy), else the preview copy. */
  async function full(c) {
    if (!c || c.generate) return null;
    if (c.remote && originals) try { return await mb.loadImage(mb.url(c.remote, 0)); } catch { /* fall back */ }
    if (c.remote && !c.image) try { return await mb.loadImage(mb.url(c.remote, 1200)); } catch { return null; }
    return c.image || null;
  }

  try {
    say(originals ? 'Fetching full-size artwork…' : 'Laying out…');
    const art = {};
    for (const p of PARTS) art[p] = await full(S.chosen[p]);
    const k = PRINT_SCALE;
    const front = render('front', art.front, places.front, album, null, k);
    step('Laying out the back inlay');
    const tray = await jpeg(render('tray', art.tray, places.tray, album, art.front, k));
    step('Laying out the disc label');
    const disc = withDisc ? await jpeg(render('disc', art.disc, places.disc, album, art.front, k)) : null;
    step('Laying out the front');
    let frontPiece = null;
    const booklet = [];
    if (scans.length) {
      const images = [];
      for (const c of scans) { const img = await full(c); if (img) images.push(img); step(`Fetched booklet page ${images.length} of ${scans.length}`); }
      const pages = bookletPages(bookletFront(front), images);
      for (const [l, r] of impose(pages.length)) booklet.push(await jpeg(bookletSide(l < 0 ? null : pages[l], r < 0 ? null : pages[r], k)));
      front.width = front.height = 0;
    } else frontPiece = await jpeg(front);

    const bytes = layout({
      paperW: letter ? 215.9 : 210, paperH: letter ? 279.4 : 297,
      front: frontPiece, tray, disc, booklet, caption: `${album.artist} – ${album.title}`,
    });
    if (S.pdfUrl) URL.revokeObjectURL(S.pdfUrl);
    S.pdfUrl = URL.createObjectURL(new Blob([bytes], { type: 'application/pdf' }));
    const a = $('download');
    a.href = S.pdfUrl;
    a.download = `${album.artist} - ${album.title} (covers).pdf`.replace(/[\\/:*?"<>|]+/g, '_');
    a.hidden = false;
    say(`Ready: ${(bytes.length / 1048576).toFixed(1)} MB, ` + (booklet.length
      ? `the tray inlay${withDisc ? ' and disc label' : ''}, then ${booklet.length / 2} booklet sheet${booklet.length === 2 ? '' : 's'} to print double-sided.`
      : `the front and tray inlay${withDisc ? ', then the disc label' : ''}.`));
  } catch (err) {
    status($('print-status'), 'Couldn\'t make the PDF: ' + (err.message || err), true);
  } finally {
    button.disabled = false;
    bar.hidden = true;
  }
}

// ------------------------------------------------------------------ start

// A shared link names its release: #release=<MBID> (a disc ID works too).
function fromHash() {
  const id = mb.parseId(decodeURIComponent(location.hash.replace(/^#(release=)?/, '')));
  if (id && !(id.release && id.release === S.release?.id)) lookup(id);
}
window.addEventListener('hashchange', fromHash);
fromHash();
