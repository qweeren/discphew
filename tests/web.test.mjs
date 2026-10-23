// Checks for the web version of the Cover Printer (web/): disc IDs, FLAC tags, imposition and the PDF.
//
//   node --test tests/web.test.mjs

import test from 'node:test';
import assert from 'node:assert/strict';
import { toc, discId, readFlac, albumFromFiles } from '../web/js/flac.js';
import { impose, time } from '../web/js/render.js';
import { parseId, plain, url, describe } from '../web/js/musicbrainz.js';
import { layout } from '../web/js/pdf.js';

test('disc ID of files laid out as a CD', async () => {
  // MusicBrainz's documented example (also libdiscid's test): offsets 150 … 80489, lead-out 95462.
  const t = toc([15213, 16951, 14278, 16822, 17075, 14973]);
  assert.deepEqual(t, [1, 6, 95462, 150, 15363, 32314, 46592, 63414, 80489]);
  assert.equal(await discId(t), '49HHV7Eb8UKF3aQiNmu1GR8vKTY-');
});

/** A FLAC file's metadata: STREAMINFO, Vorbis comments, then a few bytes standing in for audio. */
function flac(name, rate, samples, tags) {
  const enc = new TextEncoder();
  const comments = Object.entries(tags).map(([k, v]) => enc.encode(`${k}=${v}`));
  const vendor = enc.encode('test');
  const vc = new Uint8Array(4 + vendor.length + 4 + comments.reduce((n, c) => n + 4 + c.length, 0));
  const dv = new DataView(vc.buffer);
  let p = 0;
  dv.setUint32(p, vendor.length, true); vc.set(vendor, p + 4); p += 4 + vendor.length;
  dv.setUint32(p, comments.length, true); p += 4;
  for (const c of comments) { dv.setUint32(p, c.length, true); vc.set(c, p + 4); p += 4 + c.length; }
  const si = new Uint8Array(34);
  si[10] = rate >> 12; si[11] = (rate >> 4) & 0xff; si[12] = ((rate & 0xf) << 4) | 0x02;      // then channels − 1 = 1 (stereo)
  si[13] = Math.floor(samples / 2 ** 32) & 0x0f;
  new DataView(si.buffer).setUint32(14, samples % 2 ** 32);
  const block = (type, last, body) => [new Uint8Array([(last ? 0x80 : 0) | type, body.length >> 16, (body.length >> 8) & 0xff, body.length & 0xff]), body];
  return new File([enc.encode('fLaC'), ...block(0, false, si), ...block(4, true, vc), new Uint8Array(16)], name);
}

test('FLAC tags, lengths and the album they make', async () => {
  const one = await readFlac(flac('01.flac', 44100, 44100 * 200, { TITLE: 'Ünïcode title', TRACKNUMBER: '1/2' }));
  assert.equal(one.rate, 44100);
  assert.equal(one.samples, 44100 * 200);
  assert.equal(one.tags.TITLE, 'Ünïcode title');
  const big = await readFlac(flac('long.flac', 96000, 2 ** 33 + 5, {}));
  assert.equal(big.samples, 2 ** 33 + 5, '36-bit sample counts');

  const album = await albumFromFiles([
    flac('b.flac', 44100, 588 * 1000 + 1, { TRACKNUMBER: '2', TITLE: 'Second', ALBUM: 'Album', ALBUMARTIST: 'Band', MUSICBRAINZ_ALBUMID: 'abc' }),
    flac('a.flac', 44100, 588 * 100, { TRACKNUMBER: '1', TITLE: 'First', ARTIST: 'Band', DATE: '1999-01-01' }),
    new File(['not audio'], 'cover.jpg'),
  ]);
  assert.deepEqual(album.tracks.map(t => t.title), ['First', 'Second'], 'in track order');
  assert.equal(album.artist, 'Band');
  assert.equal(album.year, '1999');
  assert.equal(album.releaseId, 'abc');
  assert.deepEqual(album.sectors, [300, 1001], 'rounded up to whole sectors, at least 4 seconds');
  await assert.rejects(albumFromFiles([new File(['x'], 'x.mp3')]));
});

test('booklet imposition', () => {
  const show = n => impose(n).map(([l, r]) => `${l}|${r}`).join(' ');
  assert.equal(show(4), '3|0 1|2');
  assert.equal(show(8), '7|0 1|6 5|2 3|4');
  assert.equal(show(2), '1|0 -1|-1', 'the last page stays on the back');
  assert.equal(show(6), '5|0 1|-1 -1|2 3|4', 'blanks before the back cover');
  assert.equal(show(1), '-1|0 -1|-1');
  for (const n of [4, 8, 12, 16, 20]) assert.deepEqual(impose(n).flat().sort((a, b) => a - b), [...Array(n).keys()]);
});

test('links, IDs and names', () => {
  assert.deepEqual(parseId('https://musicbrainz.org/release/E8C3FF98-75f0-383f-a7d1-35db67661d89/cover-art'), { release: 'e8c3ff98-75f0-383f-a7d1-35db67661d89' });
  assert.deepEqual(parseId(' 49HHV7Eb8UKF3aQiNmu1GR8vKTY- '), { discId: '49HHV7Eb8UKF3aQiNmu1GR8vKTY-' });
  assert.equal(parseId('ok computer'), null);
  assert.equal(plain("OK Computer (Collector's Edition)"), 'okcomputer');
  assert.equal(plain('Björk Homogénic'), 'bjorkhomogenic');
  const img = { original: 'https://o.png', sizes: { 250: 'https://250', 500: 'https://500' } };
  assert.equal(url(img, 400), 'https://500');
  assert.equal(url(img, 1200), 'https://o.png');
  assert.equal(url(img, 0), 'https://o.png');
  assert.equal(describe({ artist: 'A', title: 'T', date: '2001', country: 'GB', mediumCount: 1, format: 'CD', trackCount: 9 }), 'A – T  ·  2001  ·  GB  ·  CD, 9 tracks');
  assert.equal(time(3725), '1:02:05');
  assert.equal(time(65), '1:05');
});

test('PDF structure', () => {
  const jpeg = new Uint8Array([0xff, 0xd8, 1, 2, 3, 0xff, 0xd9]);
  const px = mm => Math.round(mm * 300 / 25.4);
  const piece = (w, h) => ({ jpeg, w: px(w), h: px(h) });
  const bytes = layout({
    paperW: 215.9, paperH: 279.4, front: piece(126, 126), tray: piece(157, 124), disc: piece(124, 124),
    booklet: [piece(246, 126), piece(246, 126)], caption: 'Some Band – An Album (Ünï)',
  });
  const text = Buffer.from(bytes).toString('latin1');
  assert.ok(text.startsWith('%PDF-1.4\n'));
  assert.ok(text.trimEnd().endsWith('%%EOF'));
  assert.equal(text.match(/\/Type \/Page\b/g).length, 4, 'front + tray, disc, two booklet sides');
  assert.ok(text.includes('/MediaBox [0 0 612 792]'), 'US Letter in points');
  assert.ok(text.includes('/MediaBox [0 0 792 612]'), 'booklet sides on landscape pages');
  assert.equal(text.match(/\/Filter \/DCTDecode/g).length, 5);
  assert.ok(text.includes('(Some Band \\226 An Album \\(\\334n\\357\\)'), 'caption in WinAnsi, escaped');
  const xref = Number(text.match(/startxref\n(\d+)/)[1]);
  assert.ok(text.slice(xref).startsWith('xref\n'));
  const offsets = [...text.slice(xref).matchAll(/(\d{10}) 00000 n \n/g)].map(m => Number(m[1]));
  assert.equal(offsets.length, Number(text.match(/\/Size (\d+)/)[1]) - 1);
  offsets.forEach((off, i) => assert.ok(text.slice(off).startsWith(`${i + 1} 0 obj\n`), `object ${i + 1}`));
});
