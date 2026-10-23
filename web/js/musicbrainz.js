// MusicBrainz, the Cover Art Archive and the iTunes Search API, straight from the browser: all three allow
// cross-origin requests, so the page needs no server of its own.

const MB = 'https://musicbrainz.org/ws/2';

// MusicBrainz allows one request a second per client. Every call waits its turn here.
let lastRequest = 0;
let queue = Promise.resolve();

function paced(fn) {
  const run = queue.then(async () => {
    const wait = lastRequest + 1100 - Date.now();
    if (wait > 0) await new Promise(r => setTimeout(r, wait));
    lastRequest = Date.now();
    return fn();
  });
  queue = run.catch(() => {});
  return run;
}

/** A MusicBrainz web service call. Null if MusicBrainz has no such thing; backs off and retries when rate limited. */
async function mb(path) {
  for (let attempt = 0; ; attempt++) {
    const res = await paced(() => fetch(MB + path + (path.includes('?') ? '&' : '?') + 'fmt=json', { headers: { Accept: 'application/json' } }));
    if (res.status === 404) return null;
    if ((res.status === 503 || res.status === 429) && attempt < 3) {
      await new Promise(r => setTimeout(r, 1500 * (attempt + 1)));
      continue;
    }
    if (!res.ok) throw new Error(`MusicBrainz answered ${res.status}`);
    return res.json();
  }
}

const credit = o => (o?.['artist-credit'] || []).map(c => (c.name || '') + (c.joinphrase || '')).join('');

/** A release as the page uses it, with the tracks of one medium. */
function match(rel, medium) {
  const media = rel.media || [];
  const m = {
    id: rel.id,
    title: rel.title || '',
    artist: credit(rel),
    date: rel.date || '',
    country: rel.country || '',
    groupId: rel['release-group']?.id || '',
    barcode: rel.barcode || '',
    format: medium?.format || '',
    mediumPosition: medium?.position || 1,
    mediumCount: Math.max(1, media.length),
    trackCount: medium?.['track-count'] ?? medium?.tracks?.length ?? 0,
    tracks: (medium?.tracks || []).map((t, i) => ({
      number: i + 1,
      title: t.title || t.recording?.title || '',
      artist: credit(t) || credit(rel),
      length: (t.length ?? t.recording?.length ?? 0) / 1000,
    })),
  };
  m.year = m.date.slice(0, 4);
  return m;
}

export function describe(r) {
  const bits = [`${r.artist} – ${r.title}`];
  if (r.date) bits.push(r.date);
  if (r.country) bits.push(r.country);
  if (r.mediumCount > 1) bits.push(`disc ${r.mediumPosition}/${r.mediumCount}`);
  if (r.format) bits.push(r.trackCount ? `${r.format}, ${r.trackCount} tracks` : r.format);
  return bits.join('  ·  ');
}

const UUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;
const DISC_ID = /^[A-Za-z0-9._]{27}-$/;

/** What a pasted link or ID points at: a release, or a disc ID. */
export function parseId(text) {
  const s = text.trim();
  if (DISC_ID.test(s)) return { discId: s };
  const m = s.match(UUID);
  return m ? { release: m[0].toLowerCase() } : null;
}

/** One release by its MBID, with the medium numbered `disc`, else the one with `tracks` tracks. */
export async function release(id, disc = 0, tracks = 0) {
  const rel = await mb(`/release/${id}?inc=recordings+artist-credits+release-groups`);
  if (!rel) return null;
  const media = rel.media || [];
  const medium = media.find(m => disc > 0 && m.position === disc) || media.find(m => tracks > 0 && (m.tracks || []).length === tracks) || media[0];
  return match(rel, medium);
}

/** Releases with this disc ID (or, given the TOC, close matches); exact pressings. */
export async function byDiscId(discId, toc = '') {
  const root = await mb(`/discid/${encodeURIComponent(discId)}?cdstubs=no&inc=recordings+artist-credits+release-groups${toc ? '&toc=' + toc : ''}`);
  return (root?.releases || []).map(rel => {
    const media = rel.media || [];
    const medium = media.find(m => (m.discs || []).some(d => d.id === discId)) || media[0];
    return match(rel, medium);
  });
}

/** Releases matching an artist and title, best first. Search results carry no track lists. */
export async function search(artist, album, tracks = 0) {
  const phrase = s => '"' + s.trim().replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
  if (!album.trim()) return [];
  let q = 'release:' + phrase(album);
  if (artist.trim() && artist !== 'Various Artists') q += ' AND artist:' + phrase(artist);
  const root = await mb('/release/?limit=25&query=' + encodeURIComponent(q));
  return (root?.releases || []).filter(r => (r.score ?? 0) >= 60).map(rel => {
    const media = rel.media || [];
    return match(rel, media.find(m => tracks > 0 && m['track-count'] === tracks) || media[0]);
  });
}

// ------------------------------------------------------------------ artwork

const https = u => (u || '').replace(/^http:\/\//i, 'https://');

/** Every Cover Art Archive image for the release, else for its release group; front first, then in case order. */
export async function coverArt(r) {
  for (const path of [`release/${r.id}`, r.groupId && `release-group/${r.groupId}`]) {
    if (!path) continue;
    const res = await fetch('https://coverartarchive.org/' + path);
    if (res.status === 404) continue;
    if (!res.ok) throw new Error(`Cover Art Archive answered ${res.status}`);
    const images = ((await res.json()).images || []).map(o => {
      const types = o.types?.length ? o.types : o.front ? ['Front'] : [];
      const sizes = {};
      for (const [k, v] of Object.entries(o.thumbnails || {})) {
        const n = parseInt(k, 10) || (k === 'small' ? 250 : k === 'large' ? 500 : 0);
        if (n && !sizes[n]) sizes[n] = https(v);
      }
      return { id: 'caa-' + o.id, types, comment: o.comment || '', source: 'Cover Art Archive', original: https(o.image), sizes };
    });
    if (!images.length) continue;
    const order = ['Front', 'Back', 'Tray', 'Medium', 'Booklet', 'Spine'];
    const rank = i => { const n = order.findIndex(t => is(i, t)); return n < 0 ? order.length : n; };
    return images.map((img, i) => [img, i]).sort((a, b) => rank(a[0]) - rank(b[0]) || a[1] - b[1]).map(x => x[0]);
  }
  return [];
}

export const is = (img, type) => img.types.some(t => t.toLowerCase() === type.toLowerCase());

export function label(img) {
  const kind = img.types.length ? img.types.join(' + ') : 'Image';
  return img.comment ? `${kind} · ${img.comment}` : kind;
}

/** The smallest copy at least `size` pixels across, else the original (size 0: always the original). */
export function url(img, size) {
  if (size > 0) for (const k of Object.keys(img.sizes).map(Number).sort((a, b) => a - b)) if (k >= size) return img.sizes[k];
  return img.original || Object.values(img.sizes).pop() || '';
}

/** A name reduced to letters and digits, without a trailing "(Deluxe Edition)" or "[Remaster]". */
export const plain = s => (s || '').replace(/\s*[([][^)\]]*[)\]]\s*$/, '').normalize('NFD').replace(/[^\p{L}\p{N}]/gu, '').toLowerCase();

/** Barcodes to look the album up by: the release's own, then the commonest among the album's other pressings. */
export async function barcodes(r) {
  const counts = new Map();
  if (r.barcode) counts.set(r.barcode, Infinity);
  if (r.groupId) {
    const root = await mb(`/release?release-group=${r.groupId}&limit=100`);
    for (const rel of root?.releases || []) if (rel.barcode) counts.set(rel.barcode, (counts.get(rel.barcode) || 0) + 1);
  }
  return [...counts].sort((a, b) => b[1] - a[1]).map(x => x[0]).slice(0, 4);
}

const big = (r, id) => {
  const u = r.artworkUrl100;
  return {
    id: 'itunes-' + (id || r.collectionId), types: ['Front'], comment: '', source: 'iTunes',
    original: u.replace('100x100', '3000x3000'), sizes: { 600: u.replace('100x100', '600x600'), 1200: u.replace('100x100', '1200x1200') },
  };
};

/**
 * The best front cover iTunes has for the album, usually 3000 px, or null. By barcode first: on iPhones Apple
 * sends the search (but not the barcode lookup) off to the Music app instead of answering it.
 */
export async function itunes(artist, album, upcs = []) {
  for (const upc of upcs) {
    try {
      const hit = ((await (await fetch('https://itunes.apple.com/lookup?upc=' + encodeURIComponent(upc))).json()).results || [])
        .find(r => (r.artworkUrl100 || '').includes('100x100'));
      if (hit) return big(hit);
    } catch { /* try the next, then the search */ }
  }
  if (!album?.trim()) return null;
  let results;
  try {
    const res = await fetch('https://itunes.apple.com/search?media=music&entity=album&limit=15&term=' + encodeURIComponent(`${artist} ${album}`.trim()));
    results = (await res.json()).results || [];
  } catch { return null; }
  const wantAlbum = plain(album), wantArtist = plain(artist);
  const hits = results.filter(r => (r.artworkUrl100 || '').includes('100x100'))
    .map(r => ({ r, album: plain(r.collectionName), artist: plain(r.artistName) }))
    .filter(x => x.album && (x.album === wantAlbum || x.album.startsWith(wantAlbum) || wantAlbum.startsWith(x.album)))
    .sort((a, b) => (a.album === wantAlbum ? 0 : 1) - (b.album === wantAlbum ? 0 : 1)
      || artistRank(a.artist, wantArtist) - artistRank(b.artist, wantArtist));
  return hits.length ? big(hits[0].r) : null;
}

const artistRank = (a, want) => !want || a === want ? 0 : a.includes(want) || want.includes(a) ? 1 : 2;

/**
 * Downloads an image into something a canvas can draw without being tainted: the bytes are fetched (the archive's
 * redirects to archive.org all allow it) and decoded from a local blob.
 */
export async function loadImage(src) {
  const res = await fetch(src);
  if (!res.ok) throw new Error(`Image download failed (${res.status})`);
  return decode(await res.blob());
}

/** Decodes an image file or blob. */
export function decode(blob) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    const u = URL.createObjectURL(blob);
    img.onload = () => { URL.revokeObjectURL(u); resolve(img); };
    img.onerror = () => { URL.revokeObjectURL(u); reject(new Error("That isn't an image this browser can read.")); };
    img.src = u;
  });
}
