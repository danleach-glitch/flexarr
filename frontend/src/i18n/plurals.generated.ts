// Generated from languages/cldr/plurals.json by `make strings`. Don't edit.

export type PluralCategory = 'zero' | 'one' | 'two' | 'few' | 'many' | 'other'
export interface Operands { n: number; i: number; v: number; w: number; f: number; t: number; e: number }
type Rule = (o: Operands) => PluralCategory
const md = (x: number, m: number) => x % m
const rng = (x: number, lo: number, hi: number) => Number.isInteger(x) && x >= lo && x <= hi

const r0: Rule = (o) => {
  if (o.n === 1) return 'one'
  return 'other'
}
const r1: Rule = (o) => {
  if (rng(o.n, 0, 1)) return 'one'
  return 'other'
}
const r2: Rule = (o) => {
  if (o.i === 0 || o.n === 1) return 'one'
  return 'other'
}
const r3: Rule = (o) => {
  if (o.n === 0) return 'zero'
  if (o.n === 1) return 'one'
  if (o.n === 2) return 'two'
  if (rng(md(o.n, 100), 3, 10)) return 'few'
  if (rng(md(o.n, 100), 11, 99)) return 'many'
  return 'other'
}
const r4: Rule = (o) => {
  if (o.i === 1 && o.v === 0) return 'one'
  return 'other'
}
const r5: Rule = (o) => {
  if (md(o.n, 10) === 1 && !(md(o.n, 100) === 11)) return 'one'
  if (rng(md(o.n, 10), 2, 4) && !(rng(md(o.n, 100), 12, 14))) return 'few'
  if (md(o.n, 10) === 0 || rng(md(o.n, 10), 5, 9) || rng(md(o.n, 100), 11, 14)) return 'many'
  return 'other'
}
const r6: Rule = (o) => {
  if (o.n === 0) return 'zero'
  if (o.n === 1) return 'one'
  return 'other'
}
const r7: Rule = () => 'other'
const r8: Rule = (o) => {
  if (md(o.n, 10) === 1 && !(md(o.n, 100) === 11 || md(o.n, 100) === 71 || md(o.n, 100) === 91)) return 'one'
  if (md(o.n, 10) === 2 && !(md(o.n, 100) === 12 || md(o.n, 100) === 72 || md(o.n, 100) === 92)) return 'two'
  if ((rng(md(o.n, 10), 3, 4) || md(o.n, 10) === 9) && !(rng(md(o.n, 100), 10, 19) || rng(md(o.n, 100), 70, 79) || rng(md(o.n, 100), 90, 99))) return 'few'
  if (!(o.n === 0) && md(o.n, 1000000) === 0) return 'many'
  return 'other'
}
const r9: Rule = (o) => {
  if ((o.v === 0 && md(o.i, 10) === 1 && !(md(o.i, 100) === 11)) || (md(o.f, 10) === 1 && !(md(o.f, 100) === 11))) return 'one'
  if ((o.v === 0 && rng(md(o.i, 10), 2, 4) && !(rng(md(o.i, 100), 12, 14))) || (rng(md(o.f, 10), 2, 4) && !(rng(md(o.f, 100), 12, 14)))) return 'few'
  return 'other'
}
const r10: Rule = (o) => {
  if (o.i === 1 && o.v === 0) return 'one'
  if ((o.e === 0 && !(o.i === 0) && md(o.i, 1000000) === 0 && o.v === 0) || !(rng(o.e, 0, 5))) return 'many'
  return 'other'
}
const r11: Rule = (o) => {
  if ((o.v === 0 && (o.i === 1 || o.i === 2 || o.i === 3)) || (o.v === 0 && !(md(o.i, 10) === 4 || md(o.i, 10) === 6 || md(o.i, 10) === 9)) || (!(o.v === 0) && !(md(o.f, 10) === 4 || md(o.f, 10) === 6 || md(o.f, 10) === 9))) return 'one'
  return 'other'
}
const r12: Rule = (o) => {
  if (o.i === 1 && o.v === 0) return 'one'
  if (rng(o.i, 2, 4) && o.v === 0) return 'few'
  if (!(o.v === 0)) return 'many'
  return 'other'
}
const r13: Rule = (o) => {
  if (o.n === 0) return 'zero'
  if (o.n === 1) return 'one'
  if (o.n === 2) return 'two'
  if (o.n === 3) return 'few'
  if (o.n === 6) return 'many'
  return 'other'
}
const r14: Rule = (o) => {
  if (o.n === 1 || (!(o.t === 0) && (o.i === 0 || o.i === 1))) return 'one'
  return 'other'
}
const r15: Rule = (o) => {
  if ((o.v === 0 && md(o.i, 100) === 1) || md(o.f, 100) === 1) return 'one'
  if ((o.v === 0 && md(o.i, 100) === 2) || md(o.f, 100) === 2) return 'two'
  if ((o.v === 0 && rng(md(o.i, 100), 3, 4)) || rng(md(o.f, 100), 3, 4)) return 'few'
  return 'other'
}
const r16: Rule = (o) => {
  if (o.n === 1) return 'one'
  if ((o.e === 0 && !(o.i === 0) && md(o.i, 1000000) === 0 && o.v === 0) || !(rng(o.e, 0, 5))) return 'many'
  return 'other'
}
const r17: Rule = (o) => {
  if ((o.i === 0 || o.i === 1)) return 'one'
  return 'other'
}
const r18: Rule = (o) => {
  if (rng(o.i, 0, 1)) return 'one'
  if ((o.e === 0 && !(o.i === 0) && md(o.i, 1000000) === 0 && o.v === 0) || !(rng(o.e, 0, 5))) return 'many'
  return 'other'
}
const r19: Rule = (o) => {
  if (o.n === 1) return 'one'
  if (o.n === 2) return 'two'
  if (rng(o.n, 3, 6)) return 'few'
  if (rng(o.n, 7, 10)) return 'many'
  return 'other'
}
const r20: Rule = (o) => {
  if ((o.n === 1 || o.n === 11)) return 'one'
  if ((o.n === 2 || o.n === 12)) return 'two'
  if ((rng(o.n, 3, 10) || rng(o.n, 13, 19))) return 'few'
  return 'other'
}
const r21: Rule = (o) => {
  if (o.v === 0 && md(o.i, 10) === 1) return 'one'
  if (o.v === 0 && md(o.i, 10) === 2) return 'two'
  if (o.v === 0 && (md(o.i, 100) === 0 || md(o.i, 100) === 20 || md(o.i, 100) === 40 || md(o.i, 100) === 60 || md(o.i, 100) === 80)) return 'few'
  if (!(o.v === 0)) return 'many'
  return 'other'
}
const r22: Rule = (o) => {
  if ((o.i === 1 && o.v === 0) || (o.i === 0 && !(o.v === 0))) return 'one'
  if (o.i === 2 && o.v === 0) return 'two'
  return 'other'
}
const r23: Rule = (o) => {
  if ((o.t === 0 && md(o.i, 10) === 1 && !(md(o.i, 100) === 11)) || (md(o.t, 10) === 1 && !(md(o.t, 100) === 11))) return 'one'
  return 'other'
}
const r24: Rule = (o) => {
  if (o.n === 1) return 'one'
  if (o.n === 2) return 'two'
  return 'other'
}
const r25: Rule = (o) => {
  if (o.n === 0) return 'zero'
  if (o.n === 1) return 'one'
  if ((md(o.n, 100) === 2 || md(o.n, 100) === 22 || md(o.n, 100) === 42 || md(o.n, 100) === 62 || md(o.n, 100) === 82) || (md(o.n, 1000) === 0 && (rng(md(o.n, 100000), 1000, 20000) || md(o.n, 100000) === 40000 || md(o.n, 100000) === 60000 || md(o.n, 100000) === 80000)) || (!(o.n === 0) && md(o.n, 1000000) === 100000)) return 'two'
  if ((md(o.n, 100) === 3 || md(o.n, 100) === 23 || md(o.n, 100) === 43 || md(o.n, 100) === 63 || md(o.n, 100) === 83)) return 'few'
  if (!(o.n === 1) && (md(o.n, 100) === 1 || md(o.n, 100) === 21 || md(o.n, 100) === 41 || md(o.n, 100) === 61 || md(o.n, 100) === 81)) return 'many'
  return 'other'
}
const r26: Rule = (o) => {
  if (o.n === 0) return 'zero'
  if ((o.i === 0 || o.i === 1) && !(o.n === 0)) return 'one'
  return 'other'
}
const r27: Rule = (o) => {
  if (md(o.n, 10) === 1 && !(rng(md(o.n, 100), 11, 19))) return 'one'
  if (rng(md(o.n, 10), 2, 9) && !(rng(md(o.n, 100), 11, 19))) return 'few'
  if (!(o.f === 0)) return 'many'
  return 'other'
}
const r28: Rule = (o) => {
  if (md(o.n, 10) === 0 || rng(md(o.n, 100), 11, 19) || (o.v === 2 && rng(md(o.f, 100), 11, 19))) return 'zero'
  if ((md(o.n, 10) === 1 && !(md(o.n, 100) === 11)) || (o.v === 2 && md(o.f, 10) === 1 && !(md(o.f, 100) === 11)) || (!(o.v === 2) && md(o.f, 10) === 1)) return 'one'
  return 'other'
}
const r29: Rule = (o) => {
  if ((o.v === 0 && md(o.i, 10) === 1 && !(md(o.i, 100) === 11)) || (md(o.f, 10) === 1 && !(md(o.f, 100) === 11))) return 'one'
  return 'other'
}
const r30: Rule = (o) => {
  if (o.i === 1 && o.v === 0) return 'one'
  if (!(o.v === 0) || o.n === 0 || (!(o.n === 1) && rng(md(o.n, 100), 1, 19))) return 'few'
  return 'other'
}
const r31: Rule = (o) => {
  if (o.n === 1) return 'one'
  if (o.n === 2) return 'two'
  if (o.n === 0 || rng(md(o.n, 100), 3, 10)) return 'few'
  if (rng(md(o.n, 100), 11, 19)) return 'many'
  return 'other'
}
const r32: Rule = (o) => {
  if (o.i === 1 && o.v === 0) return 'one'
  if (o.v === 0 && rng(md(o.i, 10), 2, 4) && !(rng(md(o.i, 100), 12, 14))) return 'few'
  if ((o.v === 0 && !(o.i === 1) && rng(md(o.i, 10), 0, 1)) || (o.v === 0 && rng(md(o.i, 10), 5, 9)) || (o.v === 0 && rng(md(o.i, 100), 12, 14))) return 'many'
  return 'other'
}
const r33: Rule = (o) => {
  if (o.v === 0 && md(o.i, 10) === 1 && !(md(o.i, 100) === 11)) return 'one'
  if (o.v === 0 && rng(md(o.i, 10), 2, 4) && !(rng(md(o.i, 100), 12, 14))) return 'few'
  if ((o.v === 0 && md(o.i, 10) === 0) || (o.v === 0 && rng(md(o.i, 10), 5, 9)) || (o.v === 0 && rng(md(o.i, 100), 11, 14))) return 'many'
  return 'other'
}
const r34: Rule = (o) => {
  if (md(o.n, 10) === 1 && !(md(o.n, 100) === 11)) return 'one'
  if (o.n === 2) return 'two'
  if (!(o.n === 2) && rng(md(o.n, 10), 2, 9) && !(rng(md(o.n, 100), 11, 19))) return 'few'
  if (!(o.f === 0)) return 'many'
  return 'other'
}
const r35: Rule = (o) => {
  if (o.i === 0 || o.n === 1) return 'one'
  if (rng(o.n, 2, 10)) return 'few'
  return 'other'
}
const r36: Rule = (o) => {
  if ((o.n === 0 || o.n === 1) || (o.i === 0 && o.f === 1)) return 'one'
  return 'other'
}
const r37: Rule = (o) => {
  if (o.v === 0 && md(o.i, 100) === 1) return 'one'
  if (o.v === 0 && md(o.i, 100) === 2) return 'two'
  if ((o.v === 0 && rng(md(o.i, 100), 3, 4)) || !(o.v === 0)) return 'few'
  return 'other'
}
const r38: Rule = (o) => {
  if (rng(o.n, 0, 1) || rng(o.n, 11, 99)) return 'one'
  return 'other'
}

export const pluralRules: Record<string, Rule> = {
  'af': r0,
  'ak': r1,
  'am': r2,
  'an': r0,
  'ar': r3,
  'ars': r3,
  'as': r2,
  'asa': r0,
  'ast': r4,
  'az': r0,
  'bal': r0,
  'be': r5,
  'bem': r0,
  'bez': r0,
  'bg': r0,
  'bho': r1,
  'blo': r6,
  'bm': r7,
  'bn': r2,
  'bo': r7,
  'br': r8,
  'brx': r0,
  'bs': r9,
  'ca': r10,
  'ce': r0,
  'ceb': r11,
  'cgg': r0,
  'chr': r0,
  'ckb': r0,
  'cs': r12,
  'csw': r1,
  'cv': r6,
  'cy': r13,
  'da': r14,
  'de': r4,
  'doi': r2,
  'dsb': r15,
  'dv': r0,
  'dz': r7,
  'ee': r0,
  'el': r0,
  'en': r4,
  'eo': r0,
  'es': r16,
  'et': r4,
  'eu': r0,
  'fa': r2,
  'ff': r17,
  'fi': r4,
  'fil': r11,
  'fo': r0,
  'fr': r18,
  'fur': r0,
  'fy': r4,
  'ga': r19,
  'gd': r20,
  'gl': r10,
  'gsw': r0,
  'gu': r2,
  'guw': r1,
  'gv': r21,
  'ha': r0,
  'haw': r0,
  'he': r22,
  'hi': r2,
  'hnj': r7,
  'hr': r9,
  'hsb': r15,
  'hu': r0,
  'hy': r17,
  'ia': r4,
  'id': r7,
  'ie': r4,
  'ig': r7,
  'ii': r7,
  'io': r4,
  'is': r23,
  'it': r10,
  'iu': r24,
  'ja': r7,
  'jbo': r7,
  'jgo': r0,
  'jmc': r0,
  'jv': r7,
  'jw': r7,
  'ka': r0,
  'kab': r17,
  'kaj': r0,
  'kcg': r0,
  'kde': r7,
  'kea': r7,
  'kk': r0,
  'kkj': r0,
  'kl': r0,
  'km': r7,
  'kn': r2,
  'ko': r7,
  'kok': r2,
  'kok-Latn': r2,
  'ks': r0,
  'ksb': r0,
  'ksh': r6,
  'ku': r0,
  'kw': r25,
  'ky': r0,
  'lag': r26,
  'lb': r0,
  'lg': r0,
  'lij': r4,
  'lkt': r7,
  'lld': r10,
  'ln': r1,
  'lo': r7,
  'lt': r27,
  'lv': r28,
  'mas': r0,
  'mg': r1,
  'mgo': r0,
  'mk': r29,
  'ml': r0,
  'mn': r0,
  'mo': r30,
  'mr': r0,
  'ms': r7,
  'mt': r31,
  'my': r7,
  'nah': r0,
  'naq': r24,
  'nb': r0,
  'nd': r0,
  'ne': r0,
  'nl': r4,
  'nn': r0,
  'nnh': r0,
  'no': r0,
  'nqo': r7,
  'nr': r0,
  'nso': r1,
  'ny': r0,
  'nyn': r0,
  'om': r0,
  'or': r0,
  'os': r0,
  'osa': r7,
  'pa': r1,
  'pap': r0,
  'pcm': r2,
  'pl': r32,
  'prg': r28,
  'ps': r0,
  'pt': r18,
  'pt-PT': r10,
  'rm': r0,
  'ro': r30,
  'rof': r0,
  'ru': r33,
  'rwk': r0,
  'sah': r7,
  'saq': r0,
  'sat': r24,
  'sc': r4,
  'scn': r10,
  'sd': r0,
  'sdh': r0,
  'se': r24,
  'seh': r0,
  'ses': r7,
  'sg': r7,
  'sgs': r34,
  'sh': r9,
  'shi': r35,
  'si': r36,
  'sk': r12,
  'sl': r37,
  'sma': r24,
  'smi': r24,
  'smj': r24,
  'smn': r24,
  'sms': r24,
  'sn': r0,
  'so': r0,
  'sq': r0,
  'sr': r9,
  'ss': r0,
  'ssy': r0,
  'st': r0,
  'su': r7,
  'sv': r4,
  'sw': r4,
  'syr': r0,
  'ta': r0,
  'te': r0,
  'teo': r0,
  'tg': r2,
  'th': r7,
  'ti': r1,
  'tig': r0,
  'tk': r0,
  'tl': r11,
  'tn': r0,
  'to': r7,
  'tpi': r7,
  'tr': r0,
  'ts': r0,
  'tzm': r38,
  'ug': r0,
  'uk': r33,
  'und': r7,
  'ur': r4,
  'uz': r0,
  've': r0,
  'vec': r10,
  'vi': r2,
  'vo': r0,
  'vun': r0,
  'wa': r1,
  'wae': r0,
  'wo': r7,
  'xh': r0,
  'xog': r0,
  'yi': r4,
  'yo': r7,
  'yue': r7,
  'zh': r7,
  'zu': r2,
}
