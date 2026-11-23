# Сервер ИИ-объяснения матча

`worker.js` — Cloudflare Worker `roshiq-ai` (адрес `https://roshiq-ai.baramobatare.workers.dev/explain`).

- Принимает POST от программы ROSHIQ (заголовок `X-Roshiq-Client: desktop/<версия>`) и от сайта (Origin `https://baramobatare-hash.github.io`).
- В запросе — ID матча и факты разбора, без ника и Steam ID.
- Сервер сам берёт полный матч из OpenDota и считает факты: линия до 10:00 и соперники, время мёртвым, участие в драках, кто бил и убивал, предметы.
- Нейросеть — Cloudflare Workers AI через привязку `AI` (Settings → Bindings → Workers AI, имя `AI`). Ключей нет.
- Ответ кэшируется на неделю по матчу, игроку, языку и версии инструкции (`PROMPT_VERSION`). При изменении инструкции поднимите версию.

Как обновить: Cloudflare → Workers & Pages → roshiq-ai → Edit code → вставить `worker.js` → Deploy.
