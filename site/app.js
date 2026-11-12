/* ===========================================================================
   EHOT Launcher — логика сайта.

   Скачивание работает БЕЗ этого скрипта: кнопки в index.html ведут на файлы
   вида /downloads/EHOT-Launcher-...-setup.exe, которые лежат на самом сайте
   (Vercel их раздаёт). Никаких редиректов на GitHub.

   Что делает скрипт:
   * читает /downloads/manifest.json — версия и контрольные суммы текущей сборки;
   * спрашивает публичный репозиторий релизов; если там уже есть версия новее,
     кнопки начинают вести на её файлы в этом релизе (тоже прямое скачивание);
   * если GitHub недоступен — остаётся локальная сборка и честная подпись.
   =========================================================================== */

/** Публичный репозиторий ТОЛЬКО с установщиками (исходный код — в закрытом). */
const RELEASES_REPO = 'Timofei2506/EHOT-Launcher-Releases';
const SOURCE_REPO = 'Timofei2506/EHOT-Launcher';
const RELEASES_API = `https://api.github.com/repos/${RELEASES_REPO}/releases?per_page=5`;

const el = {
  versionPill: document.getElementById('version-pill'),
  buildVersion: document.getElementById('build-version'),
  sourceLink: document.getElementById('repo-link'),
  releasesLink: document.getElementById('releases-link')
};

const humanSize = (bytes) => (bytes ? `${(bytes / 1048576).toFixed(1)} МБ` : '');

/** «v0.2.0» → { major, minor, patch, pre } */
function parseTag(tag) {
  const m = /^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?/.exec(tag || '');
  if (!m) return null;
  return { major: +m[1], minor: +m[2], patch: +m[3], pre: m[4] || null, raw: tag };
}

function isNewer(a, b) {
  if (!a || !b) return false;
  if (a.major !== b.major) return a.major > b.major;
  if (a.minor !== b.minor) return a.minor > b.minor;
  if (a.patch !== b.patch) return a.patch > b.patch;
  return !a.pre && b.pre;
}

function pickRelease(releases) {
  const usable = (releases || []).filter((r) => !r.draft && r.assets && r.assets.length);
  if (!usable.length) return null;
  return usable
    .map((r) => ({ release: r, version: parseTag(r.tag_name) }))
    .filter((x) => x.version)
    .sort((a, b) => (isNewer(a.version, b.version) ? -1 : 1))[0];
}

/** Ищет файл нужного типа среди ассетов релиза. */
function findAsset(assets, kind) {
  const list = (assets || []).map((a) => ({ a, n: a.name.toLowerCase() }));
  const pick = (...needles) =>
    list.find((x) => needles.every((needle) => x.n.includes(needle)))?.a || null;

  switch (kind) {
    case 'win': return (pick('win') && (pick('win', '.exe') || pick('win', '.zip'))) || null;
    case 'mac-arm': return pick('osx', 'arm64') || pick('mac', 'arm64');
    case 'mac-x64': return pick('osx', 'x64') || pick('osx', 'intel');
    default: return null;
  }
}

function setVersionLabel(label) {
  if (el.versionPill) el.versionPill.textContent = label;
}

/** Подставляет ссылки, подпись «файл · размер» и версию. */
function applyLinks(assets, version, date) {
  document.querySelectorAll('[data-asset]').forEach((link) => {
    const asset = findAsset(assets, link.dataset.asset);
    if (!asset) return;
    link.href = asset.browser_download_url || asset.url;
    const size = humanSize(asset.size);
    if (size) {
      let meta = link.parentElement.querySelector('.dl-meta');
      if (!meta) {
        meta = document.createElement('p');
        meta.className = 'dl-meta';
        link.insertAdjacentElement('afterend', meta);
      }
      meta.textContent = `${asset.name} · ${size}`;
    }
  });

  const cta = document.getElementById('cta-main');
  const win = findAsset(assets, 'win');
  if (cta && win) cta.href = win.browser_download_url || win.url;

  if (version) {
    setVersionLabel(`Актуальная версия ${version.raw}${version.pre ? ' (beta)' : ''}`);
    if (el.buildVersion && date) {
      el.buildVersion.textContent =
        `версия ${version.raw} · ${new Date(date).toLocaleDateString('ru-RU')}`;
    }
  }
}

/** Подпись суммы из SHA256SUMS.txt в блок «проверка файла». */
function showChecksum(text, fileName) {
  const line = (text || '').split('\n').find((l) => l.includes(fileName));
  const box = document.getElementById('checksum');
  const wrap = document.getElementById('checksum-line');
  if (line && box && wrap) {
    box.textContent = line.trim().split(/\s+/)[0];
    wrap.hidden = false;
  }
}

async function loadLocalManifest() {
  try {
    const response = await fetch('downloads/manifest.json', { cache: 'no-cache' });
    if (!response.ok) throw new Error(String(response.status));
    const manifest = await response.json();

    setVersionLabel(`Версия ${manifest.version}`);
    if (el.buildVersion) {
      el.buildVersion.textContent =
        `версия ${manifest.version} · ${new Date(manifest.publishedAt).toLocaleDateString('ru-RU')}`;
    }
    document.querySelectorAll('[data-asset]').forEach((link) => {
      const file = (manifest.files || []).find((f) => f.kind === link.dataset.asset);
      if (!file) return;
      link.href = `downloads/${file.name}`;
      let meta = link.parentElement.querySelector('.dl-meta');
      if (!meta) {
        meta = document.createElement('p');
        meta.className = 'dl-meta';
        link.insertAdjacentElement('afterend', meta);
      }
      meta.textContent = `${file.name} · ${humanSize(file.size)}`;
    });

    const cta = document.getElementById('cta-main');
    const win = (manifest.files || []).find((f) => f.kind === 'win');
    if (cta && win) cta.href = `downloads/${win.name}`;

    if (win && win.sha256) {
      const box = document.getElementById('checksum-local');
      if (box) {
        box.querySelector('code').textContent = win.sha256;
        box.hidden = false;
      }
    }
    return manifest;
  } catch (error) {
    // Локальная копия не найдена — не выдумываем версию.
    console.warn('manifest.json недоступен:', error.message);
    setVersionLabel('Сборки на сайте и GitHub Releases');
    return null;
  }
}

async function preferNewerRelease(localManifest) {
  const link = el.releasesLink;
  if (link) link.href = `https://github.com/${RELEASES_REPO}/releases`;

  try {
    const response = await fetch(RELEASES_API, { headers: { Accept: 'application/vnd.github+json' } });
    if (!response.ok) throw new Error(`GitHub ответил ${response.status}`);
    const best = pickRelease(await response.json());
    if (!best) throw new Error('нет опубликованных релизов');

    const local = localManifest ? parseTag(localManifest.version) : null;
    if (!local || isNewer(best.version, local)) {
      // На GitHub лежит версия новее той, что вшита в сайт — ведём на неё.
      applyLinks(best.release.assets, best.version, best.release.published_at);
      if (link) link.href = best.release.html_url;
      console.info(`На сайте v${localManifest?.version ?? '?'}, на GitHub ${best.version.raw} — показываю GitHub.`);
    }

    // Суммы уже показаны из манифеста (downloads/manifest.json) — он всегда свежий.
    // SHA256SUMS.txt с GitHub напрямую не читаем: у релизных файлов нет CORS-заголовков,
    // и запрос честно падал бы в консоль. Ссылка на файл при этом остаётся рабочей.
  } catch (error) {
    // Это нормальный, ожидаемый путь: сайт живёт на своих файлах.
    console.info('Релизы GitHub недоступны, работаем на файлах сайта:', error.message);
  }
}

async function start() {
  if (el.sourceLink) el.sourceLink.href = `https://github.com/${SOURCE_REPO}`;
  const manifest = await loadLocalManifest();
  await preferNewerRelease(manifest);
}

start();
