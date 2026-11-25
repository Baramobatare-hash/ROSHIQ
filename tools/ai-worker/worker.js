// ROSHIQ — ИИ-объяснение матча.
// Cloudflare Worker. Программа ROSHIQ и сайт присылают ID матча и факты разбора. Сервер сам берёт полный матч
// из OpenDota (линия, соперники, время мёртвым, участие в драках, предметы) и просит нейросеть Cloudflare Workers AI
// объяснить ошибки. Ключей нет: модель вызывается через привязку AI (env.AI).

const PROMPT_VERSION = 'v7';
const ALLOWED_ORIGINS = ['https://baramobatare-hash.github.io'];
const MODELS = [
  '@cf/meta/llama-3.3-70b-instruct-fp8-fast',
  '@cf/google/gemma-3-12b-it',
  '@cf/meta/llama-3.1-8b-instruct-fast',
];
const MAX_BODY = 60000;
const OD = 'https://api.opendota.com/api';
// справочники героев и предметов — из открытого репозитория dotaconstants (из него их собирает и OpenDota)
const DC = 'https://raw.githubusercontent.com/odota/dotaconstants/master/build';

const SYSTEM = `Ты — тренер по Dota 2, который разбирает один матч одного игрока. Пиши по-русски, обращайся на «ты».

Тебе дают два блока фактов:
- client_facts — разбор программы ROSHIQ: итог, KDA, сравнение с игроками на этом герое (перцентили), золото команды по минутам, смерти с разменом или без, тайминги предметов, выводы анализатора (analyzer_findings);
- match_facts — посчитано по полному матчу: ранг, примерная роль (likely_role), линия до 10:00 и соперники на ней, время мёртвым, участие в драках, кто больше всего бил и убивал, крупные покупки, итоговые предметы свои и врагов.

КАК ДУМАТЬ
1. Сначала пойми роль: likely_role и место по ценности. Саппорта не ругай за мало добиваний и низкую ценность; его оценивают по смертям, участию в драках, вардам, контролю.
2. Ошибки уже найдены: match_facts.diagnosis (отсортированы по важности) и пункты «минус:» в client_facts.analyzer_findings, плюс смерти «без размена» из client_facts. Возьми 2–3 самых важных из них. НЕ добавляй ошибок, которых там нет. Если diagnosis пуст и минусов нет — скажи, что явных ошибок в цифрах не видно, и дай одну задачу на рост.
   Плюсы бери из match_facts.good и пунктов «плюс:».
3. Каждую ошибку свяжи с последствием, а совет бери из «Что делать:» в этом же пункте diagnosis (перескажи своими словами). Для смертей без размена совет такой: перед тем как зайти в драку или на чужую территорию, убедись, что рядом союзники и есть обзор.
4. Ориентиры (средние ранги, не абсолют): добивания к 10:00 — керри 45–60, мид 40–55, оффлейн 25–40, саппорт 0–15. Время мёртвым больше 15% матча — много. Участие в драках у кора ниже 50%, у саппорта ниже 60% — мало.
5. Предметы упоминай только из фактов. Предлагать предмет можно, только если в фактах есть причина (например, больше всего урона от одного героя) — и пиши осторожно: «можно было рассмотреть».
6. Если матч выигран — всё равно найди, что улучшить, но начни с того, что сработало.

ЗАПРЕТЫ
- Ничего не выдумывай: никаких событий, цифр, предметов, способностей и героев, которых нет в фактах.
- Не путай отставание с перевесом: «отстаёт» — команда игрока беднее врагов.
- Никаких пустых советов вроде «работай над игрой», «будь внимательнее», «улучши лайнинг» — только конкретное действие, которое можно сделать в следующем матче.
- Не пересказывай проценты без смысла: не «хуже, чем у 78% игроков», а что это значит на деле.
- Без английских терминов, если есть русское слово (крипы, а не creep'ы).
- Саппорту не советуй байбэки и не ругай его за фарм. Не хвали за то, чего нет в good или «плюс:».
- Не пиши общие фразы «старайся умирать реже», «участвуй активнее» без конкретного действия.
- Не придумывай последствия, которых нет в фактах: не пиши «из-за этого враги взяли Рошана / снесли бараки», если Рошана и строений нет в фактах. Последствие — это то, что видно в цифрах (отставание по золоту, время мёртвым, проигранная линия).
- Минуты пиши только те, что есть в фактах.

ФОРМАТ (не больше 180 слов)
Первая строка — итог матча (match_facts.result) в одном предложении: главная причина, взятая из первого пункта diagnosis.
Строка «Главные ошибки:» и 2–3 пункта «- …»: что случилось (минута или цифра) → чем это обернулось → как сделать иначе.
Строка «Что получилось:» и 1 пункт «- …» — ТОЛЬКО если в match_facts.good или в «плюс:» что-то есть; иначе пропусти этот раздел целиком (не пиши «нет успехов»). KDA сам по себе не хвали.
Строка «В следующей игре:» и 1 пункт «- …» — одна главная конкретная задача.`;

const RANKS = ['', 'Рекрут', 'Страж', 'Рыцарь', 'Герой', 'Легенда', 'Властелин', 'Божество', 'Титан'];
const LANE_ROLE = { 1: 'лёгкая линия', 2: 'центральная линия', 3: 'сложная линия', 4: 'лес' };

function clock(s) { const n = s < 0, a = Math.abs(Math.round(s)); return (n ? '-' : '') + Math.floor(a / 60) + ':' + String(a % 60).padStart(2, '0'); }
function nf(n) { return Math.round(Math.abs(n)).toLocaleString('ru-RU'); }
function at(a, i) { return Array.isArray(a) && a.length > i ? a[i] : null; }
function rankName(t) { if (!t) return null; const r = RANKS[Math.floor(t / 10)]; return r ? (t >= 80 ? r : r + ' ' + (t % 10)) : null; }

function serverFacts(m, me, C) {
  const hero = id => (C.heroes[id] && C.heroes[id].name) || '?';
  const rad = me.player_slot < 128;
  const mine = m.players.filter(p => (p.player_slot < 128) === rad);
  const foes = m.players.filter(p => (p.player_slot < 128) !== rad);
  const f = {};
  const dur = m.duration || 0;
  if (typeof m.radiant_win === 'boolean') f.result = m.radiant_win === rad ? 'победа' : 'поражение';

  // ранг: свой, если открыт, иначе средний по матчу
  const rk = rankName(me.rank_tier) || rankName(m.avg_rank_tier);
  if (rk) f.rank = rk;

  // роль по факту: место по ценности в команде и доля добиваний
  const byNw = [...mine].sort((a, b) => (b.net_worth || 0) - (a.net_worth || 0));
  const nwRank = byNw.indexOf(me) + 1;
  f.net_worth_place_in_team = `${nwRank} из 5`;
  f.lane = (LANE_ROLE[me.lane_role] || 'неизвестно') + (me.is_roaming ? ', роумил' : '');

  // линия 0–10: соперники и союзники на той же линии
  if (me.lane && me.lane !== 4 && Array.isArray(me.lh_t) && me.lh_t.length > 10) {
    const nw10 = p => at(p.networth_t, 10) ?? at(p.gold_t, 10);
    const opp = foes.filter(p => p.lane === me.lane);
    const allies = mine.filter(p => p.lane === me.lane && p !== me);
    const side = [me, ...allies].reduce((s, p) => s + (nw10(p) || 0), 0);
    const other = opp.reduce((s, p) => s + (nw10(p) || 0), 0);
    const deaths10 = (me.deaths_log || []).filter(d => d.time <= 600).length
      || foes.reduce((s, p) => s + (p.kills_log || []).filter(k => k.key === C.heroes[me.hero_id]?.npc && k.time <= 600).length, 0);
    f.lane_phase_10min = {
      you: `добивания ${at(me.lh_t, 10)}, не отдал ${at(me.dn_t, 10) ?? 0}, ценность ${nf(nw10(me) || 0)}, смертей до 10:00: ${deaths10}`,
      allies_on_lane: allies.map(p => `${hero(p.hero_id)}: добивания ${at(p.lh_t, 10)}, ценность ${nf(nw10(p) || 0)}`),
      enemies_on_lane: opp.map(p => `${hero(p.hero_id)}: добивания ${at(p.lh_t, 10)}, ценность ${nf(nw10(p) || 0)}`),
      result: opp.length === 0 ? 'против тебя на линии никого не было' :
        (side - other >= 0 ? `твоя сторона линии впереди на ${nf(side - other)} ценности` : `твоя сторона линии отстаёт на ${nf(other - side)} ценности`),
    };
  }

  // время мёртвым
  if (me.life_state && dur) {
    const ls = me.life_state, total = Object.values(ls).reduce((a, b) => a + b, 0);
    const dead = (ls['1'] || 0) + (ls['2'] || 0);
    if (total > 0) f.time_dead = `${clock(dead * dur / total)} (${Math.round(dead / total * 100)}% матча)`;
  }
  if (me.teamfight_participation != null) f.teamfight_participation_pct = Math.round(me.teamfight_participation * 100);
  if (Array.isArray(me.buyback_log)) f.buybacks = me.buyback_log.length;

  // кто бил и кто убивал
  const npcToName = {};
  for (const id in C.heroes) npcToName[C.heroes[id].npc] = C.heroes[id].name;
  if (me.damage_taken) {
    const selfNpc = C.heroes[me.hero_id]?.npc;
    const top = Object.entries(me.damage_taken).filter(([k]) => npcToName[k] && k !== selfNpc).sort((a, b) => b[1] - a[1]).slice(0, 3);
    if (top.length) f.most_damage_taken_from = top.map(([k, v]) => `${npcToName[k]} ${nf(v)}`);
  }
  if (me.killed_by) {
    const top = Object.entries(me.killed_by).filter(([k]) => npcToName[k]).sort((a, b) => b[1] - a[1]).slice(0, 3);
    if (top.length) f.killed_by = top.map(([k, v]) => `${npcToName[k]} ×${v}`);
  }

  // предметы: крупные покупки по времени и итог
  const big = (me.purchase_log || []).filter(b => b.time > 0 && (C.items[b.key]?.cost || 0) >= 1800);
  if (big.length) f.big_item_timings = big.slice(0, 8).map(b => `${clock(b.time)} ${C.items[b.key]?.name || b.key}`);
  const fin = [0, 1, 2, 3, 4, 5].map(i => C.itemIds[me['item_' + i]]).filter(Boolean).map(k => C.items[k]?.name || k);
  if (fin.length) f.final_items = fin;
  const enemyFinal = foes.map(p => `${hero(p.hero_id)}: ` + [0, 1, 2, 3, 4, 5].map(i => C.itemIds[p['item_' + i]]).filter(k => k && (C.items[k]?.cost || 0) >= 2000).map(k => C.items[k]?.name || k).join(', '));
  f.enemy_core_items = enemyFinal;

  // поддержка и вклад в объекты
  const lh10 = at(me.lh_t, 10);
  const supp = (lh10 != null && lh10 <= 15) || nwRank >= 4;
  f.likely_role = supp ? 'саппорт (мало добиваний на линии)' : 'кор (фармящий герой)';
  if (supp) {
    f.support_stats = `обсерверов ${me.obs_placed ?? '?'}, сентри ${me.sen_placed ?? '?'}, стаков ${me.camps_stacked ?? '?'}, контроль ${me.stuns != null ? Math.round(me.stuns) + ' с' : '?'}`;
  } else {
    f.core_stats = `урон по строениям ${nf(me.tower_damage || 0)}, добивания в минуту ${dur ? (me.last_hits / (dur / 60)).toFixed(1) : '?'}`;
  }
  // командный контекст: кто лучший по ценности у врагов
  const fTop = [...foes].sort((a, b) => (b.net_worth || 0) - (a.net_worth || 0))[0];
  if (fTop) f.enemy_richest = `${hero(fTop.hero_id)} — ${nf(fTop.net_worth || 0)}`;

  // ДИАГНОЗ: ошибки и плюсы по правилам (вес — насколько сильно повлияло), нейросеть только объясняет их
  const bad = [], good = [];
  const mins = dur / 60;
  if (me.kills != null) f.kda = `${me.kills}/${me.deaths}/${me.assists}`;
  // 1. время мёртвым
  if (me.life_state && dur) {
    const ls = me.life_state, total = Object.values(ls).reduce((a, b) => a + b, 0);
    const pct = total ? ((ls['1'] || 0) + (ls['2'] || 0)) / total * 100 : 0;
    if (pct >= 15) bad.push([pct >= 22 ? 9 : 7, `Много времени мёртвым: ${f.time_dead}. Каждая смерть — потерянные золото, опыт и карта без тебя. Что делать: прежде чем идти вперёд, посмотри на карту — если двух-трёх врагов не видно, не заходи за реку без варда или союзника; фарми ближе к своим вышкам.`]);
    else if (pct <= 7 && dur > 1200) good.push(`Почти не умирал: мёртвым всего ${f.time_dead}.`);
  }
  // 2. смерти на линии
  const lp = f.lane_phase_10min;
  const d10 = lp ? Number((lp.you.match(/смертей до 10:00: (\d+)/) || [])[1] || 0) : 0;
  if (d10 >= 2) bad.push([8, `${d10} ${d10 % 10 >= 2 && d10 % 10 <= 4 && (d10 % 100 < 12 || d10 % 100 > 14) ? 'смерти' : 'смертей'} до 10:00 — линия начиналась с отставания. Что делать: держись за своими крипами, не иди на размен, когда у соперника больше здоровья, и отходи, если соперники пропали с линии — это часто значит, что идут убивать.`]);
  // 3. итог линии
  if (lp && lp.enemies_on_lane.length) {
    const nw10 = p => at(p.networth_t, 10) ?? at(p.gold_t, 10) ?? 0;
    const opp = foes.filter(p => p.lane === me.lane), allies = mine.filter(p => p.lane === me.lane);
    const diff = allies.reduce((s, p) => s + nw10(p), 0) - opp.reduce((s, p) => s + nw10(p), 0);
    const vs = opp.map(p => hero(p.hero_id)).join(' и ');
    if (diff <= -800) bad.push([diff <= -2000 ? 8 : 6, `Линия против ${vs} проиграна: к 10:00 твоя сторона отстаёт на ${nf(-diff)} ценности. Что делать: если линия тяжёлая — не отдавай смертями опыт, забирай безопасные волны и лесные лагеря рядом, зови помощь от мида или саппорта.`]);
    else if (diff >= 800) good.push(`Линия против ${vs} выиграна: к 10:00 твоя сторона впереди на ${nf(diff)} ценности.`);
  }
  // 4. добивания на линии у кора — к норме его линии
  if (!supp && lh10 != null) {
    const norm = me.lane_role === 2 ? 40 : me.lane_role === 3 ? 25 : 45;
    if (lh10 < norm * 0.7) bad.push([6, `Добиваний к 10:00 — ${lh10} при ориентире около ${norm} для этой линии: мало фарма в начале. Что делать: на линии главное — добивать: следи за здоровьем крипов и не трать время на размены, когда идёт волна.`]);
    else if (lh10 >= norm) good.push(`Хорошо фармил линию: ${lh10} добиваний к 10:00.`);
  }
  // 5. участие в драках
  if (f.teamfight_participation_pct != null) {
    const tp = f.teamfight_participation_pct, low = supp ? 60 : 50;
    if (tp < low) bad.push([5, `Участие в убийствах команды ${tp}% — ${supp ? 'саппорту' : 'кору'} стоит быть в драках чаще (ориентир от ${low}%). Что делать: после 15 минут держись ближе к команде; когда команда собирается у вышки или Рошана — иди к ней, а не фарми на другом краю карты.`]);
    else if (tp >= 70) good.push(`Был почти во всех драках: участие ${tp}%.`);
  }
  // 6. варды у саппорта
  if (supp && me.obs_placed != null && mins >= 20) {
    if (me.obs_placed < mins / 6) bad.push([4, `Мало обзора: ${me.obs_placed} обсерверов за ${Math.round(mins)} минут (ориентир — хотя бы один каждые 5–6 минут). Что делать: забирай обсервер из лавки, как только он появился, и ставь его перед драками и у Рошана.`]);
    else if (me.obs_placed >= mins / 4) good.push(`Хороший обзор: ${me.obs_placed} обсерверов.`);
  }
  // 7. кто больше всего бил — повод для защитного предмета
  if (me.damage_taken) {
    const selfNpc = C.heroes[me.hero_id]?.npc;
    const rows = Object.entries(me.damage_taken).filter(([k]) => npcToName[k] && k !== selfNpc);
    const total = rows.reduce((a, [, v]) => a + v, 0);
    const [k, v] = rows.sort((a, b) => b[1] - a[1])[0] || [];
    if (k && total && v / total >= 0.35) bad.push([3, `Больше всего урона (${Math.round(v / total * 100)}%) тебе нанёс ${npcToName[k]} — против него нужен план: позиция дальше, защитный предмет или ждать, пока он потратит способности.`]);
  }
  f.diagnosis = bad.sort((a, b) => b[0] - a[0]).map(x => x[1]);
  f.good = good;
  return f;
}

// Проверка ответа по фактам: строка с минутой, героем или объектом карты, которых нет в фактах, выкидывается.
// Модель иногда дописывает «враги взяли Рошана на 25:00» — такого пункта в ответе быть не должно.
const OBJECTIVES = [
  [/рошан|аегис|аегида|roshan|aegis/i, /рошан|roshan|aegis|аегис/i],
  [/барак|barrack/i, /барак|barrack/i],
  [/\bтрон|throne/i, /трон|throne|ancient/i],
  [/торментор|терзател|tormentor/i, /торментор|терзател|tormentor/i],
  [/смок|smoke/i, /смок|smoke/i],
  [/байб[еэ]к|выкуп|buyback/i, /байб[еэ]к|выкуп|buyback/i],
];
function verify(text, facts, mf, C) {
  const known = JSON.stringify([facts, mf]);
  const times = new Set();
  for (const t of known.matchAll(/(\d{1,2}):(\d{2})/g)) times.add(+t[1] * 60 + +t[2]);
  const timeOk = sec => sec % 300 === 0 || [...times].some(k => Math.abs(k - sec) <= 60);
  const lowKnown = known.toLowerCase();
  const heroNames = C ? Object.values(C.heroes).map(h => h.name).filter(n => n && n.length >= 3) : [];
  const has = (hay, name) => new RegExp('(^|[^\\p{L}])' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '($|[^\\p{L}])', 'iu').test(hay);
  const dropped = [];
  const lines = text.split('\n').filter(line => {
    const t = line.trim();
    if (!t || /^\**\s*(Главные ошибки|Что получилось|В следующей игре|Main mistakes|What went well|Next game)/i.test(t)) return true;
    for (const m of t.matchAll(/\b(\d{1,2}):(\d{2})\b/g)) if (!timeOk(+m[1] * 60 + +m[2])) { dropped.push('время ' + m[0]); return false; }
    for (const [say, need] of OBJECTIVES) if (say.test(t) && !need.test(lowKnown)) { dropped.push('объект: ' + t.match(say)[0]); return false; }
    for (const n of heroNames) if (has(t, n) && !has(known, n)) { dropped.push('герой ' + n); return false; }
    return true;
  });
  let out = lines.join('\n');
  // если из «Главных ошибок» ничего не осталось — подставляем найденные правилами ошибки
  const m = out.match(/(Главные ошибки:?\**[ \t]*\n)([\s\S]*?)(?=^\s*\**\s*(?:Что получилось|В следующей игре)|(?![\s\S]))/im);
  if (m && !/^\s*[-•*]/m.test(m[2]) && mf && mf.diagnosis && mf.diagnosis.length)
    out = out.replace(m[0], m[1] + mf.diagnosis.slice(0, 2).map(d => '- ' + d).join('\n') + '\n' + m[2]);
  return { text: out, dropped };
}

// Убрать раздел «Что получилось», если плюсов нет (модель иногда пишет «нет успехов»)
function tidy(text, mf, facts) {
  const plus = (mf && mf.good && mf.good.length) || (Array.isArray(facts.analyzer_findings) && facts.analyzer_findings.some(x => /^плюс:/.test(x)));
  const lines = text.split('\n');
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    if (/^\s*\**\s*Что получилось/i.test(lines[i])) {
      let j = i + 1;
      const block = [];
      while (j < lines.length && !/^\s*\**\s*В следующей игре/i.test(lines[j])) block.push(lines[j++]);
      const empty = !plus || block.every(l => !l.trim() || /нет\s+(явных|заметных|особых)?\s*(успех|плюс)/i.test(l));
      if (empty) { i = j - 1; continue; }
    }
    out.push(lines[i]);
  }
  return out.join('\n').replace(/\n{3,}/g, '\n\n').trim();
}

function cors(origin) {
  const ok = ALLOWED_ORIGINS.includes(origin);
  return {
    'Access-Control-Allow-Origin': ok ? origin : ALLOWED_ORIGINS[0],
    'Access-Control-Allow-Methods': 'POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type',
    'Vary': 'Origin',
  };
}
function json(data, status, origin) {
  return new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json; charset=utf-8', ...cors(origin) } });
}
async function sha(text) {
  const buf = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(buf)].map(b => b.toString(16).padStart(2, '0')).join('');
}
async function odGet(path, ttl) {
  const r = await fetch(OD + path, { cf: { cacheTtl: ttl, cacheEverything: true }, headers: { 'User-Agent': 'ROSHIQ-AI/2' } });
  if (!r.ok) throw new Error('OpenDota ' + r.status);
  return r.json();
}
let CONST = null;
async function constants() {
  if (CONST) return CONST;
  const get = async name => {
    const r = await fetch(`${DC}/${name}.json`, { cf: { cacheTtl: 86400, cacheEverything: true } });
    if (!r.ok) throw new Error('dotaconstants ' + r.status);
    return r.json();
  };
  const [h, it, ids] = await Promise.all([get('heroes'), get('items'), get('item_ids')]);
  const heroes = {};
  for (const k in h) heroes[h[k].id] = { name: h[k].localized_name, npc: h[k].name };
  const items = {};
  for (const k in it) items[k] = { cost: it[k].cost || 0, name: it[k].dname || k };
  CONST = { heroes, items, itemIds: ids };
  return CONST;
}
// полный матч из OpenDota и игрок: по имени героя из фактов (надёжно), иначе по слоту
// Матч: урезанная копия от программы или сайта (body.od — они качают его из OpenDota со своего адреса),
// иначе пробуем OpenDota сами (с общих адресов Cloudflare часто отвечает 429).
async function matchFacts(body, facts) {
  const C = await constants();
  let m = body.od && Array.isArray(body.od.players) && body.od.players.length <= 10 ? body.od : null;
  if (!m) {
    const id = String(body.match || '');
    if (!/^\d{6,12}$/.test(id)) return null;
    m = await odGet('/matches/' + id, 3600);
    if (!m || !Array.isArray(m.players)) return null;
  }
  let me = Number.isInteger(body.slot) && body.od ? m.players.find(p => p.player_slot === body.slot) : null;
  const want = String(facts.hero || '').toLowerCase();
  if (!me) me = m.players.find(p => (C.heroes[p.hero_id]?.name || '').toLowerCase() === want);
  if (!me && Number.isInteger(body.slot)) me = m.players.find(p => p.player_slot === body.slot);
  if (!me) return null;
  return serverFacts(m, me, C);
}

export default {
  async fetch(request, env, ctx) {
    const origin = request.headers.get('Origin') || '';
    const url = new URL(request.url);
    if (request.method === 'OPTIONS') return new Response(null, { status: 204, headers: cors(origin) });
    if (url.pathname === '/health') {
      try { const C = await constants(); return json({ ok: true, heroes: Object.keys(C.heroes).length }, 200, origin); }
      catch (e) { return json({ ok: false, error: String(e && e.message || e) }, 200, origin); }
    }
    if (url.pathname !== '/explain' || request.method !== 'POST') return json({ error: 'Не найдено' }, 404, origin);
    // сайт ROSHIQ (по Origin) или программа ROSHIQ для Windows (без Origin, с заголовком X-Roshiq-Client: desktop/<версия>)
    const desktop = !origin && /^desktop\/[0-9.]+$/.test(request.headers.get('X-Roshiq-Client') || '');
    if (!ALLOWED_ORIGINS.includes(origin) && !desktop) return json({ error: 'Запрос не от ROSHIQ' }, 403, origin);

    const raw = await request.text();
    if (raw.length > MAX_BODY) return json({ error: 'Слишком большой запрос' }, 413, origin);
    let body;
    try { body = JSON.parse(raw); } catch { return json({ error: 'Неверный формат' }, 400, origin); }
    const facts = body && body.facts;
    if (!facts || typeof facts !== 'object' || !facts.hero) return json({ error: 'Нет данных матча' }, 400, origin);

    // кэш: один и тот же матч, игрок, язык и версия инструкции — один ответ на неделю
    const key = new Request('https://cache.roshiq/' + await sha(JSON.stringify([PROMPT_VERSION, body.match, body.slot, body.lang || 'ru', facts])));
    const cache = caches.default;
    const hit = await cache.match(key);
    if (hit) return json({ text: await hit.text(), cached: true }, 200, origin);

    let mf = null, mfErr = null;
    try { mf = await matchFacts(body, facts); if (!mf) mfErr = 'игрок или матч не найден'; } catch (e) { mf = null; mfErr = String(e && e.message || e); }

    const messages = [
      { role: 'system', content: SYSTEM + (body.lang === 'en' ? '\n\nОтвечай на английском языке.' : '') },
      { role: 'user', content: 'client_facts:\n' + JSON.stringify(facts) + '\n\nmatch_facts:\n' + (mf ? JSON.stringify(mf) : 'нет (полный матч недоступен)') },
    ];
    let text = null, lastErr = null, used = null, checked = [];
    for (const model of MODELS) {
      try {
        const res = await env.AI.run(model, { messages, max_tokens: 700, temperature: 0.25 });
        text = (res && (res.response || res.result?.response) || '').trim();
        if (text) {
          let C = null;
          try { C = await constants(); } catch { C = null; }
          const v = verify(text, facts, mf, C);
          checked = v.dropped;
          text = tidy(v.text, mf, facts); used = model; break;
        }
      } catch (e) { lastErr = e; }
    }
    if (!text) {
      const msg = String(lastErr && lastErr.message || '');
      const limit = /limit|quota|429|neurons/i.test(msg);
      return json({ error: limit ? 'Дневной лимит ИИ исчерпан. Попробуй завтра.' : 'ИИ сейчас недоступен. Попробуй позже.' }, limit ? 429 : 502, origin);
    }
    ctx.waitUntil(cache.put(key, new Response(text, { headers: { 'Cache-Control': 'public, max-age=604800' } })));
    return json({ text, v: PROMPT_VERSION, model: used, dropped: checked, match_facts: !!mf, match_facts_error: mfErr, diagnosis: mf ? mf.diagnosis : null }, 200, origin);
  },
};
