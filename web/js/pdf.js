// Just enough PDF for print templates, built in the browser: JPEG images placed as-is (PDF reads JPEG natively),
// hairlines and circles for crop and fold marks, and a line of Helvetica (built into every reader, nothing embedded).
// Positions are in millimetres from the top-left of the page. A port of discphew's Print/PdfWriter.cs.

import { BLEED, SPINE, DISC_OUTER, DISC_HOLE, PRINT_SCALE } from './render.js';

const PT = 72 / 25.4;
const N = v => String(Math.round(v * 1000) / 1000);
const ascii = s => new TextEncoder().encode(s);     // only ever ASCII: text is escaped below

// The punctuation WinAnsi keeps where Latin-1 has control codes.
const WIN_ANSI = { '‘': 0x91, '’': 0x92, '“': 0x93, '”': 0x94, '•': 0x95, '–': 0x96, '—': 0x97, '…': 0x85, '€': 0x80 };

function escape(s) {
  let out = '';
  for (const ch of s) {
    const c = ch.codePointAt(0);
    if (ch === '(' || ch === ')' || ch === '\\') out += '\\' + ch;
    else if (c >= 32 && c < 127) out += ch;
    else if (c >= 0xa0 && c <= 0xff) out += '\\' + c.toString(8).padStart(3, '0');
    else if (WIN_ANSI[ch]) out += '\\' + WIN_ANSI[ch].toString(8);
    else out += '?';
  }
  return out;
}

function stream(dict, data) {
  const head = ascii(`<< ${dict} /Length ${data.length} >>\nstream\n`), tail = ascii('\nendstream');
  const out = new Uint8Array(head.length + data.length + tail.length);
  out.set(head); out.set(data, head.length); out.set(tail, head.length + data.length);
  return out;
}

export class PdfWriter {
  constructor() { this.objects = [null, null]; this.pages = []; }        // 1 and 2 (catalog, pages) are filled in last

  add(body) { this.objects.push(typeof body === 'string' ? ascii(body) : body); return this.objects.length; }

  addPage(w, h) { this.page = { w, h, content: [], images: [] }; this.pages.push(this.page); }

  X(mm) { return mm * PT; }
  Y(mm) { return (this.page.h - mm) * PT; }

  /** A baseline JPEG (RGB) of pxW × pxH pixels, stretched over the given box. */
  image(jpeg, pxW, pxH, x, y, w, h) {
    const obj = this.add(stream(`/Type /XObject /Subtype /Image /Width ${pxW} /Height ${pxH} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode`, jpeg));
    this.page.images.push(obj);
    this.page.content.push(`q ${N(w * PT)} 0 0 ${N(h * PT)} ${N(this.X(x))} ${N(this.Y(y + h))} cm /Im${obj} Do Q`);
  }

  /** gray: 0 black … 1 white. */
  line(x1, y1, x2, y2, widthPt = 0.25, gray = 0, dashed = false) {
    this.page.content.push(`q ${N(gray)} G ${N(widthPt)} w ${dashed ? '[2 1.5] 0 d ' : ''}${N(this.X(x1))} ${N(this.Y(y1))} m ${N(this.X(x2))} ${N(this.Y(y2))} l S Q`);
  }

  circle(cx, cy, r, widthPt = 0.25, gray = 0) {
    // Four Bézier quarter-arcs; 0.5523 puts the control points where they trace a circle to within 0.03 %.
    const k = r * 0.5523, P = (x, y) => `${N(this.X(x))} ${N(this.Y(y))}`;
    this.page.content.push(`q ${N(gray)} G ${N(widthPt)} w ${P(cx + r, cy)} m ` +
      `${P(cx + r, cy - k)} ${P(cx + k, cy - r)} ${P(cx, cy - r)} c ` +
      `${P(cx - k, cy - r)} ${P(cx - r, cy - k)} ${P(cx - r, cy)} c ` +
      `${P(cx - r, cy + k)} ${P(cx - k, cy + r)} ${P(cx, cy + r)} c ` +
      `${P(cx + k, cy + r)} ${P(cx + r, cy + k)} ${P(cx + r, cy)} c S Q`);
  }

  /** One line of Helvetica centred on x, its baseline at y. Latin-1 only; anything else prints as '?'. */
  textCentered(x, y, text, sizePt, gray = 0) {
    const width = text.length * sizePt * 0.5 / PT;       // Helvetica's widths aren't to hand: half an em a glyph
    this.page.content.push(`BT ${N(gray)} g /F1 ${N(sizePt)} Tf ${N(this.X(x - width / 2))} ${N(this.Y(y))} Td (${escape(text)}) Tj ET`);
  }

  bytes() {
    const font = this.add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>');
    const kids = this.pages.map(p => {
      const content = this.add(stream('', ascii(p.content.join('\n') + '\n')));
      const xobjects = p.images.map(i => `/Im${i} ${i} 0 R `).join('');
      return this.add(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${N(p.w * PT)} ${N(p.h * PT)}] ` +
        `/Resources << /XObject << ${xobjects}>> /Font << /F1 ${font} 0 R >> >> /Contents ${content} 0 R >>`);
    });
    this.objects[0] = ascii('<< /Type /Catalog /Pages 2 0 R >>');
    this.objects[1] = ascii(`<< /Type /Pages /Kids [${kids.map(k => k + ' 0 R').join(' ')}] /Count ${kids.length} >>`);

    const parts = [ascii('%PDF-1.4\n'), new Uint8Array([0x25, 0xe2, 0xe3, 0xcf, 0xd3, 0x0a])];
    let pos = parts[0].length + parts[1].length;
    const offsets = [];
    this.objects.forEach((o, i) => {
      offsets.push(pos);
      for (const part of [ascii(`${i + 1} 0 obj\n`), o, ascii('\nendobj\n')]) { parts.push(part); pos += part.length; }
    });
    let xref = `xref\n0 ${this.objects.length + 1}\n0000000000 65535 f \n`;
    for (const o of offsets) xref += String(o).padStart(10, '0') + ' 00000 n \n';
    xref += `trailer\n<< /Size ${this.objects.length + 1} /Root 1 0 R >>\nstartxref\n${pos}\n%%EOF\n`;
    parts.push(ascii(xref));
    const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
    let at = 0;
    for (const p of parts) { out.set(p, at); at += p.length; }
    return out;
  }
}

const GAP = 12, MARK = 5;     // between parts on a page (clear of each other's marks); crop mark length
const ACTUAL = 'print at actual size (100 %), not “fit to page”';

/**
 * The print-ready PDF. Each piece is { jpeg, w, h } in pixels at 300 DPI, bleed included: the front over the tray on one
 * page (or, in booklet mode, the tray over the disc label), the disc label on the next, then one landscape page per
 * booklet side. Every part gets crop marks a bleed outside its cut, and dashed fold marks at its spines or centre.
 */
export function layout({ paperW = 210, paperH = 297, front = null, tray = null, disc = null, booklet = [], caption = '' }) {
  const pdf = new PdfWriter();
  const first = [];
  if (front) first.push([front, 'square']);
  if (tray) first.push([tray, 'tray']);
  if (!front && disc) first.push([disc, 'disc']);
  if (first.length) stack(pdf, paperW, paperH, first, (caption ? caption + '   ·   ' : '') + ACTUAL);
  if (front && disc) stack(pdf, paperW, paperH, [[disc, 'disc']], 'Disc label   ·   ' + ACTUAL);
  booklet.forEach((side, i) => stack(pdf, paperH, paperW, [[side, 'booklet']],
    `Booklet sheet ${Math.floor(i / 2) + 1} of ${Math.ceil(booklet.length / 2)}, ${i % 2 ? 'inside' : 'outside'}   ·   print double-sided, flipping on the short edge, at actual size`));
  return pdf.bytes();
}

function stack(pdf, pw, ph, items, caption) {
  pdf.addPage(pw, ph);
  const b = BLEED;
  const trims = items.map(([p]) => [p.w / PRINT_SCALE - 2 * b, p.h / PRINT_SCALE - 2 * b]);
  let y = (ph - trims.reduce((s, t) => s + t[1], 0) - GAP * (items.length - 1)) / 2;
  items.forEach(([p, kind], i) => {
    const [w, h] = trims[i], x = (pw - w) / 2;
    pdf.image(p.jpeg, p.w, p.h, x - b, y - b, w + 2 * b, h + 2 * b);
    for (const cx of [x, x + w]) for (const cy of [y, y + h]) {
      const sx = cx === x ? -1 : 1, sy = cy === y ? -1 : 1;
      pdf.line(cx + sx * b, cy, cx + sx * (b + MARK), cy);
      pdf.line(cx, cy + sy * b, cx, cy + sy * (b + MARK));
    }
    const fold = fx => { pdf.line(fx, y - b - MARK, fx, y - b, 0.25, 0, true); pdf.line(fx, y + h + b, fx, y + h + b + MARK, 0.25, 0, true); };
    if (kind === 'tray') { fold(x + SPINE); fold(x + w - SPINE); }
    if (kind === 'booklet') fold(x + w / 2);
    if (kind === 'disc') {
      // Cut lines for scissors; a die-cut label sheet ignores them.
      pdf.circle(x + w / 2, y + h / 2, DISC_OUTER / 2, 0.2, 0.55);
      pdf.circle(x + w / 2, y + h / 2, DISC_HOLE / 2, 0.2, 0.55);
    }
    y += h + GAP;
  });
  pdf.textCentered(pw / 2, ph - 4, caption, 6.5, 0.35);
}

/** A canvas as a JPEG piece for the layout. Frees the canvas's pixels afterwards: phones have little memory to spare. */
export async function jpeg(canvas, quality = 0.92) {
  const blob = await new Promise((resolve, reject) => canvas.toBlob(b => b ? resolve(b) : reject(new Error('The browser ran out of memory drawing the page.')), 'image/jpeg', quality));
  const piece = { jpeg: new Uint8Array(await blob.arrayBuffer()), w: canvas.width, h: canvas.height };
  canvas.width = canvas.height = 0;
  return piece;
}
