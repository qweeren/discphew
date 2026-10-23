// Jewel case inserts drawn on a canvas at any scale (k pixels per mm): the on-screen preview and the 300 DPI
// print use the same code. A port of discphew's Print/CoverRender.cs.

export const BLEED = 3;                 // artwork carries on this far past every cut
export const SPINE = 6.5;               // each side of the tray inlay
export const DISC_OUTER = 118, DISC_HOLE = 41;
export const PAGE = 120;                // a booklet page
export const PRINT_SCALE = 300 / 25.4;  // pixels per mm at 300 DPI

export const TRIM = { front: [120, 120], tray: [151, 118], disc: [DISC_OUTER, DISC_OUTER] };
export const withBleed = part => [TRIM[part][0] + 2 * BLEED, TRIM[part][1] + 2 * BLEED];

export const newPlacement = () => ({ zoom: 1, x: 0, y: 0, angle: 0 });
export const isDefault = p => p.zoom === 1 && p.x === 0 && p.y === 0 && p.angle === 0;

const FONT = '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif';
const INK = '#f4f2ee';

/** Something drawable with an optional source rectangle: an image, a canvas, or a part of one. */
function src(s) {
  if (s && s.img) return s;
  const w = s.naturalWidth || s.width, h = s.naturalHeight || s.height;
  return { img: s, sx: 0, sy: 0, sw: w, sh: h };
}

export const size = s => { const r = src(s); return [r.sw, r.sh]; };

function canvas(wMm, hMm, k) {
  const c = document.createElement('canvas');
  c.width = Math.max(1, Math.round(wMm * k));
  c.height = Math.max(1, Math.round(hMm * k));
  const g = c.getContext('2d');
  g.fillStyle = '#fff';
  g.fillRect(0, 0, c.width, c.height);
  g.imageSmoothingQuality = 'high';
  return [c, g];
}

/** The art centred in the box, scaled to just cover it, then moved (mm), zoomed and turned (degrees) as placed. */
function drawArt(g, art, place, [x, y, w, h], k) {
  if (!art) return;
  const s = src(art);
  place = place || newPlacement();
  g.save();
  g.beginPath();
  g.rect(x, y, w, h);
  g.clip();
  const fit = Math.max(w / s.sw, h / s.sh) * place.zoom;
  g.translate(x + w / 2 + place.x * k, y + h / 2 + place.y * k);
  g.rotate(place.angle * Math.PI / 180);
  g.scale(fit, fit);
  g.drawImage(s.img, s.sx, s.sy, s.sw, s.sh, -s.sw / 2, -s.sh / 2, s.sw, s.sh);
  g.restore();
}

/** A scan of just the back panel (138 × 118) rather than the whole inlay with its spines (151 × 118). */
export function isBackOnly(art) {
  const [w, h] = size(art);
  return w / h < (138 / 118 + 151 / 118) / 2;
}

/**
 * One part with its bleed, at k pixels per mm. For the tray, `art` may be a scan of the whole inlay, of the back
 * panel only (the spines are then made to match), or null to generate one over a blurred `backdrop` (the front).
 */
export function render(part, art, place, album, backdrop, k) {
  const [wMm, hMm] = withBleed(part);
  const [c, g] = canvas(wMm, hMm, k);
  const full = [0, 0, c.width, c.height];
  const b = BLEED * k;
  if (part === 'front') drawArt(g, art, place, full, k);
  else if (part === 'disc') {
    drawArt(g, art || backdrop, place, full, k);
    // White outside the label and in the hole; the art runs a bleed past both cuts.
    const cx = c.width / 2, cy = c.height / 2;
    g.fillStyle = '#fff';
    g.beginPath();
    g.rect(-1, -1, c.width + 2, c.height + 2);
    g.arc(cx, cy, (DISC_OUTER / 2 + BLEED) * k, 0, Math.PI * 2);
    g.fill('evenodd');
    g.beginPath();
    g.arc(cx, cy, (DISC_HOLE / 2 - BLEED) * k, 0, Math.PI * 2);
    g.fill();
  } else {
    const spine = SPINE * k;
    const back = [b + spine, 0, c.width - 2 * (b + spine), c.height];
    if (art && !isBackOnly(art)) drawArt(g, art, place, full, k);     // a full inlay scan has its own spines
    else {
      blurred(g, art || backdrop, full);
      if (art) drawArt(g, art, place, back, k);
      else trackList(g, album, back, k);
      spines(g, album, full, k);
    }
  }
  return c;
}

/** The image blurred right out and darkened, so light text reads on it and it still matches the cover. */
function blurred(g, img, box) {
  if (!img) {
    g.fillStyle = '#1e2024';
    g.fillRect(...box);
    return;
  }
  // Shrinking to a few pixels averages the picture; stretching it back up in two steps is a smooth blur.
  const sw = 18, sh = Math.max(2, Math.round(sw * box[3] / box[2]));
  const tiny = document.createElement('canvas');
  tiny.width = sw; tiny.height = sh;
  const tg = tiny.getContext('2d');
  tg.imageSmoothingQuality = 'high';
  const mid = document.createElement('canvas');
  mid.width = sw * 8; mid.height = sh * 8;
  const mg = mid.getContext('2d');
  mg.imageSmoothingQuality = 'high';
  drawArt(mg, img, null, [0, 0, mid.width, mid.height], 1);
  tg.drawImage(mid, 0, 0, sw, sh);
  mg.drawImage(tiny, 0, 0, mid.width, mid.height);
  g.drawImage(mid, box[0], box[1], box[2], box[3]);
  g.fillStyle = 'rgba(0,0,0,0.59)';
  g.fillRect(...box);
}

const font = (pt, k, bold) => `${bold ? '700' : '400'} ${pt * 25.4 / 72 * k}px ${FONT}`;

/** The text, cut short with an ellipsis to fit `max` pixels. */
function fit(g, text, max) {
  if (max <= 0) return '';
  if (g.measureText(text).width <= max) return text;
  let lo = 0, hi = text.length;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (g.measureText(text.slice(0, mid).trimEnd() + '…').width <= max) lo = mid; else hi = mid - 1;
  }
  return lo > 0 ? text.slice(0, lo).trimEnd() + '…' : '';
}

export function time(seconds) {
  const s = Math.round(seconds), h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, sec = String(s % 60).padStart(2, '0');
  return h ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${Math.floor(s / 60)}:${sec}`;
}

export const total = album => album.tracks.reduce((t, x) => t + (x.length || 0), 0);

function trackList(g, album, [ax, ay, aw, ah], k) {
  if (!album) return;
  const x0 = ax + 9 * k, x1 = ax + aw - 9 * k;
  let y = ay + (BLEED + 9) * k, bottom = ay + ah - (BLEED + 8) * k;
  g.textBaseline = 'middle';
  const line = (text, f, color, top, height, x = x0, w = x1 - x0, align = 'left') => {
    g.font = f;
    g.fillStyle = color;
    g.textAlign = align;
    g.fillText(fit(g, text, w), align === 'right' ? x + w : x, top + height / 2);
  };

  let h = 12 * 25.4 / 72 * k * 1.4;
  line(album.artist, font(12, k, true), INK, y, h);
  y += h;
  h = 9.5 * 25.4 / 72 * k * 1.4;
  line(album.title, font(9.5, k), 'rgba(244,242,238,0.73)', y, h);
  y += h + 5 * k;

  const bits = [];
  if (album.year) bits.push(album.year);
  if (album.tracks.length) bits.push(album.tracks.length === 1 ? '1 track' : `${album.tracks.length} tracks`);
  if (total(album) > 0) bits.push(time(total(album)));
  h = 7.5 * 25.4 / 72 * k * 1.4;
  line(bits.join('   ·   '), font(7.5, k), 'rgba(244,242,238,0.51)', bottom - h, h);
  bottom -= h + 4 * k;

  // The biggest type that fits every track, in one column or two.
  const n = album.tracks.length;
  if (!n) return;
  const gap = 7 * k;
  let pt = 8.5, cols = 1, rows = n;
  for (; pt >= 5; pt -= 0.25) {
    const fitRows = Math.floor((bottom - y) / (pt * 1.55 * 25.4 / 72 * k));
    if (fitRows >= n) { cols = 1; rows = n; break; }
    if (fitRows * 2 >= n) { cols = 2; rows = Math.ceil(n / 2); break; }
  }
  if (pt < 5) { pt = 5; cols = 2; rows = Math.ceil(n / 2); }
  const lh = pt * 1.55 * 25.4 / 72 * k;
  const colW = (x1 - x0 - gap * (cols - 1)) / cols;
  g.font = font(pt, k, true);
  const numW = g.measureText('00').width + 1.5 * k;
  g.font = font(pt, k);
  const durW = g.measureText('00:00').width + 1 * k;
  album.tracks.forEach((t, i) => {
    const cx = x0 + Math.floor(i / rows) * (colW + gap), cy = y + (i % rows) * lh;
    if (cy + lh > bottom + 0.5) return;           // more tracks than will ever fit at 5 pt
    line(String(t.number), font(pt, k, true), 'rgba(244,242,238,0.51)', cy, lh, cx, numW - 1.5 * k, 'right');
    let title = t.title || `Track ${t.number}`;
    if (t.artist && t.artist.toLowerCase() !== (album.artist || '').toLowerCase()) title += '  ·  ' + t.artist;
    line(title, font(pt, k), INK, cy, lh, cx + numW, colW - numW - durW);
    if (t.length > 0) line(time(t.length), font(pt, k), 'rgba(244,242,238,0.73)', cy, lh, cx + colW - durW, durW, 'right');
  });
}

/** Artist and title along both spines: reading up the left one and down the right, as cases are shelved. */
function spines(g, album, full, k) {
  if (!album || !(album.artist || album.title)) return;
  const b = BLEED * k, spine = SPINE * k, length = full[3] - 2 * b - 10 * k;
  const artist = (album.artist || '').toUpperCase(), sep = album.artist && album.title ? '   ·   ' : '';
  g.textBaseline = 'middle';
  g.textAlign = 'left';
  g.font = font(7, k, true);
  const a = fit(g, artist + sep, length);
  const aw = g.measureText(a).width;
  g.font = font(7, k);
  const t = fit(g, album.title || '', length - aw);
  const tw = g.measureText(t).width;
  for (const [x, angle] of [[b + spine / 2, -Math.PI / 2], [full[2] - b - spine / 2, Math.PI / 2]]) {
    g.save();
    g.translate(x, full[3] / 2);
    g.rotate(angle);
    const start = -(aw + tw) / 2;
    g.font = font(7, k, true);
    g.fillStyle = INK;
    g.fillText(a, start, 0);
    g.font = font(7, k);
    g.fillStyle = 'rgba(244,242,238,0.73)';
    g.fillText(t, start + aw, 0);
    g.restore();
  }
}

// ------------------------------------------------------------------ booklet

/**
 * The booklet's pages in reading order: the front, then the scanned pages, with two-page spreads (scanned open,
 * twice as wide as tall) cut into their pages. Pages are views onto the images, not copies.
 */
export function bookletPages(front, scans) {
  const pages = front ? [src(front)] : [];
  for (const s of scans) {
    const r = src(s);
    if (r.sw / r.sh < 1.5) { pages.push(r); continue; }
    const w = r.sw / 2;
    pages.push({ ...r, sw: w }, { ...r, sx: r.sx + w, sw: w });
  }
  return pages;
}

/**
 * Saddle-stitch imposition: which pages (0-based, -1 for a blank) go on the left and right of each printed side, for
 * `pages` pages padded to a multiple of four. The blanks go just before the last page, so the back cover stays on the
 * back. Sides come in pairs, outside then inside of each sheet, outermost sheet first.
 */
export function impose(pages) {
  const n = Math.ceil(pages / 4) * 4;
  const P = i => pages < 2 ? (i < pages ? i : -1) : i < pages - 1 ? i : i === n - 1 ? pages - 1 : -1;
  const sides = [];
  for (let s = 0; s < n / 4; s++) {
    sides.push([P(n - 1 - 2 * s), P(2 * s)]);
    sides.push([P(2 * s + 1), P(n - 2 - 2 * s)]);
  }
  return sides;
}

/** One printed side of a booklet sheet: two pages either side of the fold, with bleed round the outside. */
export function bookletSide(left, right, k) {
  const [c, g] = canvas(2 * PAGE + 2 * BLEED, PAGE + 2 * BLEED, k);
  const half = (PAGE + BLEED) * k;
  drawArt(g, left, null, [0, 0, half, c.height], k);
  drawArt(g, right, null, [half, 0, c.width - half, c.height], k);
  return c;
}

/** The rendered front less the bleed on its left: the right-hand page of the booklet's outside. */
export function bookletFront(rendered) {
  const cut = Math.round(rendered.width * BLEED / withBleed('front')[0]);
  return { img: rendered, sx: cut, sy: 0, sw: rendered.width - cut, sh: rendered.height };
}
