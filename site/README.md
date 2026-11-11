# Сайт EHOT Launcher

Статическая страница без сборки и внешних зависимостей (никаких CDN и шрифтов):
`index.html` + `styles.css` + `app.js` + `favicon.svg`.

## Что делает `app.js`

Запрашивает `https://api.github.com/repos/Timofei2506/EHOT-Launcher/releases`
и подставляет в кнопки прямые ссылки на актуальные установщики, а в шапку — версию.
Если API недоступен (репозиторий приватный, нет сети) — кнопки ведут на страницу
релизов GitHub, а версия честно помечается как неопределённая. Ничего не выдумывается.

## Публикация

**Вариант 1 (рекомендуется): Vercel + GitHub.**
1. В Vercel: Add New… → Project → Import Git Repository → `Timofei2506/EHOT-Launcher`.
2. Root Directory: `site`. Framework Preset: `Other`. Build Command и Output Directory — пустые
   (Vercel раздаёт статику как есть).
3. Deploy. Дальше каждый `git push` в `main` обновляет сайт сам.

**Вариант 2: локальная проверка.**
```bash
cd site && python3 -m http.server 8088
```
Открыть http://localhost:8088
