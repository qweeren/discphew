// FLAC metadata (tags, length, embedded cover) read from files the user picks, and the MusicBrainz disc ID of the
// CD those tracks make: lengths rounded up to whole sectors, back to back from LBA 0, as discphew's Burner writes them.

/** Reads the metadata blocks at the head of a FLAC file. Only the blocks needed are read, not the audio. */
export async function readFlac(file) {
  const at = async (pos, len) => new Uint8Array(await file.slice(pos, pos + len).arrayBuffer());
  const head = await at(0, 4);
  if (String.fromCharCode(...head) !== 'fLaC') throw new Error(`${file.name} isn't a FLAC file.`);
  const info = { name: file.name, tags: {}, rate: 0, samples: 0, picture: null };
  let pos = 4;
  for (let last = false; !last && pos < file.size;) {
    const h = await at(pos, 4);
    last = (h[0] & 0x80) !== 0;
    const type = h[0] & 0x7f, len = (h[1] << 16) | (h[2] << 8) | h[3];
    pos += 4;
    if (type === 0) {
      const b = await at(pos, len);
      info.rate = (b[10] << 12) | (b[11] << 4) | (b[12] >> 4);
      info.samples = (b[13] & 0x0f) * 2 ** 32 + (((b[14] << 24) | (b[15] << 16) | (b[16] << 8) | b[17]) >>> 0);   // 36 bits
    } else if (type === 4) {
      Object.assign(info.tags, vorbisComments(await at(pos, len)));
    } else if (type === 6 && !info.picture) {
      info.picture = picture(await at(pos, len));
    }
    pos += len;
  }
  return info;
}

function vorbisComments(b) {
  const dv = new DataView(b.buffer, b.byteOffset, b.byteLength);
  const text = new TextDecoder();
  let p = 4 + dv.getUint32(0, true);
  const count = dv.getUint32(p, true);
  p += 4;
  const tags = {};
  for (let i = 0; i < count && p + 4 <= b.length; i++) {
    const n = dv.getUint32(p, true);
    const s = text.decode(b.subarray(p + 4, p + 4 + n));
    p += 4 + n;
    const eq = s.indexOf('=');
    if (eq <= 0) continue;
    const key = s.slice(0, eq).toUpperCase(), value = s.slice(eq + 1);
    tags[key] = tags[key] ? tags[key] + '; ' + value : value;
  }
  return tags;
}

function picture(b) {
  const dv = new DataView(b.buffer, b.byteOffset, b.byteLength);
  let p = 4;
  const mimeLen = dv.getUint32(p); p += 4;
  const mime = new TextDecoder().decode(b.subarray(p, p + mimeLen)); p += mimeLen;
  p += 4 + dv.getUint32(p);       // description
  p += 16;                        // width, height, depth, colours
  const len = dv.getUint32(p); p += 4;
  return new Blob([b.slice(p, p + len)], { type: mime || 'image/jpeg' });
}

/** An album from its FLAC files: names and release ID from the tags, tracks in order, and the disc's TOC. */
export async function albumFromFiles(files) {
  const infos = [];
  for (const f of files) if (/\.flac$/i.test(f.name)) infos.push(await readFlac(f));
  if (!infos.length) throw new Error('Pick the album\'s FLAC files (one per track).');
  const num = (s, fallback) => parseInt((s || '').split('/')[0], 10) || fallback;
  infos.sort((a, b) => num(a.tags.DISCNUMBER, 1) - num(b.tags.DISCNUMBER, 1) || num(a.tags.TRACKNUMBER, 999) - num(b.tags.TRACKNUMBER, 999) || a.name.localeCompare(b.name));
  const first = key => infos.map(i => i.tags[key]).find(v => v) || '';
  const album = {
    artist: first('ALBUMARTIST') || first('ARTIST'),
    title: first('ALBUM'),
    year: first('DATE').slice(0, 4),
    releaseId: first('MUSICBRAINZ_ALBUMID'),
    disc: num(first('DISCNUMBER'), 0),
    cover: infos.map(i => i.picture).find(p => p) || null,
    tracks: infos.map((i, n) => ({
      number: num(i.tags.TRACKNUMBER, n + 1),
      title: i.tags.TITLE || i.name.replace(/\.flac$/i, '').replace(/^\s*\d{1,3}\s*[-._ ]\s*/, ''),
      artist: i.tags.ARTIST || '',
      length: i.rate ? i.samples / i.rate : 0,
    })),
  };
  album.sectors = infos.map(i => Math.max(300, Math.ceil(i.samples * 44100 / (i.rate || 44100) / 588)));   // at least 4 s, as burned
  return album;
}

/** First track, last track, lead-out and each track's start, all in sectors with the 150-sector lead-in: MusicBrainz's TOC. */
export function toc(sectors) {
  const starts = [];
  let lba = 150;
  for (const n of sectors) { starts.push(lba); lba += n; }
  return [1, sectors.length, lba, ...starts];
}

/** The MusicBrainz disc ID of a TOC: SHA-1 of its hex form, in MusicBrainz's variant of base64. */
export async function discId(t) {
  const [firstTrack, lastTrack, leadOut, ...starts] = t;
  const hex = (n, w) => n.toString(16).toUpperCase().padStart(w, '0');
  let s = hex(firstTrack, 2) + hex(lastTrack, 2) + hex(leadOut, 8);
  for (let i = 0; i < 99; i++) s += hex(starts[i] || 0, 8);
  const hash = new Uint8Array(await crypto.subtle.digest('SHA-1', new TextEncoder().encode(s)));
  return btoa(String.fromCharCode(...hash)).replace(/\+/g, '.').replace(/\//g, '_').replace(/=/g, '-');
}
