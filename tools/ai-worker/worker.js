// ROSHIQ — ИИ-объяснение матча (v2).
// Cloudflare Worker. Программа ROSHIQ и сайт присылают ID матча и факты разбора. Сервер сам берёт полный матч
// из OpenDota (линия, соперники, время мёртвым, участие в драках, предметы) и просит нейросеть Cloudflare Workers AI
// объяснить ошибки. Ключей нет: модель вызывается через привязку AI (env.AI).

const PROMPT_VERSION = 'v2';
const ALLOWED_ORIGINS = ['https://baramobatare-hash.github.io'];
const MODELS = [
  '@cf/meta/llama-3.3-70b-instruct-fp8-fast',
  '@cf/google/gemma-3-12b-it',
  '@cf/meta/llama-3.1-8b-instruct-fast',
];
const MAX_BODY = 12000;
const OD = 'https://api.opendota.com/api';

const SYSTEM = `Ты — тренер по Dota 2, который разбирает один матч одного игрока. Пиши по-русски, обращайся на «ты».

Тебе дают два блока фактов:
- client_facts — разбор программы ROSHIQ: итог, KDA, сравнение с игроками на этом герое (перцентили), золото команды по минутам, смерти с разменом или без, тайминги предметов, выводы анализатора (analyzer_findings);
- match_facts — посчитано по полному матчу: ранг, примерная роль (likely_role), линия до 10:00 и соперники на ней, время мёртвым, участие в драках, кто больше всего бил и убивал, крупные покупки, итоговые предметы свои и врагов.

КАК ДУМАТЬ
1. Сначала пойми роль: likely_role и место по ценности. Саппорта не ругай за мало добиваний и низкую ценность; его оценивают по смертям, участию в драках, вардам, контролю.
2. Найди 2–3 ошибки, которые сильнее всего повлияли на итог. Порядок важности обычно такой: смерти без размена и время мёртвым → проигранная линия → медленный фарм и поздние ключевые предметы → низкое участие в драках → предметы, не подходящие против врагов.
3. Каждую ошибку свяжи с последствием: «умер без размена на 12:40 → потерял ~1 минуту фарма и линия осталась без защиты».
4. Ориентиры (средние ранги, не абсолют): добивания к 10:00 — керри 45–60, мид 40–55, оффлейн 25–40, саппорт 0–15. Время мёртвым больше 15% матча — много. Участие в драках у кора ниже 50%, у саппорта ниже 60% — мало.
5. Предметы упоминай только из фактов. Предлагать предмет можно, только если в фактах есть причина (например, больше всего урона от одного героя) — и пиши осторожно: «можно было рассмотреть».
6. Если матч выигран — всё равно найди, что улучшить, но начни с того, что сработало.

ЗАПРЕТЫ
- Ничего не выдумывай: никаких событий, цифр, предметов, способностей и героев, которых нет в фактах.
- Не путай отставание с перевесом: «отстаёт» — команда игрока беднее врагов.
- Никаких пустых советов вроде «работай над игрой», «будь внимательнее», «улучши лайнинг» — только конкретное действие, которое можно сделать в следующем матче.
- Не пересказывай проценты без смысла: не «хуже, чем у 78% игроков», а что это значит на деле.
- Без английских терминов, если есть русское слово (крипы, а не creep'ы).

ФОРМАТ (не больше 180 слов)
Первая строка — итог матча в одном предложении: почему выиграл или проиграл.
Строка «Главные ошибки:» и 2–3 пункта «- …»: что случилось (минута или цифра) → чем это обернулось → как сделать иначе.
Строка «Что получилось:» и 1 пункт «- …» (если есть что-то хорошее в фактах).
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
  return f;
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
  const [h, it, ids] = await Promise.all([odGet('/constants/heroes', 86400), odGet('/constants/items', 86400), odGet('/constants/item_ids', 86400)]);
  const heroes = {};
  for (const k in h) heroes[h[k].id] = { name: h[k].localized_name, npc: h[k].name };
  const items = {};
  for (const k in it) items[k] = { cost: it[k].cost || 0, name: it[k].dname || k };
  CONST = { heroes, items, itemIds: ids };
  return CONST;
}
// полный матч из OpenDota и игрок: по имени героя из фактов (надёжно), иначе по слоту
async function matchFacts(body, facts) {
  const id = String(body.match || '');
  if (!/^\d{6,12}$/.test(id)) return null;
  const [C, m] = await Promise.all([constants(), odGet('/matches/' + id, 3600)]);
  if (!m || !Array.isArray(m.players)) return null;
  const want = String(facts.hero || '').toLowerCase();
  let me = m.players.find(p => (C.heroes[p.hero_id]?.name || '').toLowerCase() === want);
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
    let text = null, lastErr = null;
    for (const model of MODELS) {
      try {
        const res = await env.AI.run(model, { messages, max_tokens: 700, temperature: 0.25 });
        text = (res && (res.response || res.result?.response) || '').trim();
        if (text) break;
      } catch (e) { lastErr = e; }
    }
    if (!text) {
      const msg = String(lastErr && lastErr.message || '');
      const limit = /limit|quota|429|neurons/i.test(msg);
      return json({ error: limit ? 'Дневной лимит ИИ исчерпан. Попробуй завтра.' : 'ИИ сейчас недоступен. Попробуй позже.' }, limit ? 429 : 502, origin);
    }
    ctx.waitUntil(cache.put(key, new Response(text, { headers: { 'Cache-Control': 'public, max-age=604800' } })));
    return json({ text, v: PROMPT_VERSION, match_facts: !!mf, match_facts_error: mfErr }, 200, origin);
  },
};
