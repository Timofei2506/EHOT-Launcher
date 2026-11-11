# Сайт EHOT Launcher

Статическая страница без сборки и внешних зависимостей (никаких CDN и шрифтов):
`index.html` + `styles.css` + `app.js` + `favicon.svg`. Плюс `robots.txt` и `vercel.json`
(заголовки кэширования и безопасности).

## Что делает `app.js`

Запрашивает `https://api.github.com/repos/Timofei2506/EHOT-Launcher/releases`
и подставляет в кнопки прямые ссылки на актуальные установщики, а в шапку — версию.
Если API недоступен (репозиторий приватный, нет сети) — кнопки ведут на страницу
релизов GitHub, а версия честно помечается как неопределённая. Ничего не выдумывается.

**Важно:** пока репозиторий приватный, GitHub Releases API отвечает 404 и файлы скачать
может только владелец репозитория. Чтобы кнопки на сайте действительно качали установщики,
репозиторий должен стать публичным (это же снимет блокер автообновления лаунчера).

## Публикация на Vercel

Проект `ehot-launcher-site` уже создан в Vercel и связан с этим репозиторием
(Root Directory: `site`, Framework: Other). Первый деплой через MCP-коннектор невозможен:
Vercel требует `projectSettings`, а коннектор этот параметр не передаёт. Дальше — вручную,
два равнозначных варианта.

**Вариант А — из панели Vercel (проще, без установки программ):**
1. Открыть https://vercel.com/tgargach12-7359s-projects/ehot-launcher-site
2. Вкладка **Deployments** → кнопка **Deploy** / **Redeploy** (или **Create Deployment**).
3. Готово: адрес будет вида `ehot-launcher-site-<hash>.vercel.app`
   (после деплоя можно добавить красивый домен: Settings → Domains).

**Вариант Б — из терминала (одна команда):**
```bash
cd site
npx vercel@latest deploy --prod --yes --token ВАШ_ТОКЕН --scope tgargach12-7359s-projects
```
`--yes` принимает настройки проекта (Root Directory `site`, статика без сборки),
токен создаётся здесь: https://vercel.com/account/settings/tokens

После первого успешного деплоя каждый `git push` в `main` будет обновлять сайт
автоматически, если в проекте включён Git-автодеплой (Settings → Git).

## Локальная проверка

```bash
cd site && python3 -m http.server 8088
```
Открыть http://localhost:8088
