/* ===========================================================================
   EHOT Launcher — логика сайта.
   Данные о версии и файлах берутся из GitHub Releases API, чтобы сайт не врал
   про версию и всегда вёл на актуальный установщик.
   Если API недоступен (нет сети, репозиторий приватный) — кнопки ведут на
   страницу релизов, а версия честно помечается как неизвестная.
   =========================================================================== */

const REPO = 'Timofei2506/EHOT-Launcher';
const API = `https://api.github.com/repos/${REPO}/releases?per_page=5`;

/** Пути к установщикам, если API недоступен. */
const FALLBACK = {
  releases: `https://github.com/${REPO}/releases`,
  repo: `https://github.com/${REPO}`
};

const el = {
  versionPill: document.getElementById('version-pill'),
  buildVersion: document.getElementById('build-version'),
  ctaMain: document.getElementById('cta-main'),
  repoLink: document.getElementById('repo-link'),
  releasesLink: document.getElementById('releases-link'),
  downloads: document.getElementById('downloads')
};

const humanSize = (bytes) =>
  bytes ? `${(bytes / 1048576).toFixed(1)} МБ` : '';

/** Разбирает тег вида v0.2.0-beta.1 → { major, minor, patch, pre } */
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
  // одинаковые номера: релиз (без pre) новее предрелиза
  return !a.pre && b.pre;
}

/** Выбирает лучший релиз: последний опубликованный, предрелизы допускаем — проект в beta. */
function pickRelease(releases) {
  const usable = (releases || []).filter((r) => !r.draft && r.assets && r.assets.length);
  if (!usable.length) return null;
  return usable
    .map((r) => ({ release: r, version: parseTag(r.tag_name) }))
    .filter((x) => x.version)
    .sort((a, b) => (isNewer(a.version, b.version) ? -1 : 1))[0];
}

function findAsset(assets, kind) {
  const names = (assets || []).map((a) => ({ a, n: a.name.toLowerCase() }));
  const pick = (...needles) =>
    names.find((x) => needles.every((needle) => x.n.includes(needle)))?.a || null;

  switch (kind) {
    case 'win':      return pick('win') && (pick('win', '.exe') || pick('win', '.zip'));
    case 'mac-arm':  return pick('osx', 'arm64') || pick('mac', 'arm64');
    case 'mac-x64':  return pick('osx', 'x64') || pick('osx', 'intel');
    default:         return null;
  }
}

function paint(version, release) {
  const label = version ? `Версия ${version.raw}` : 'Версия из GitHub';
  if (el.versionPill) el.versionPill.textContent = version
    ? `Актуальная версия ${version.raw}${version.pre ? ' (beta)' : ''}`
    : 'Сборки на GitHub Releases';

  if (el.buildVersion) {
    el.buildVersion.textContent = version
      ? `версия ${version.raw} · ${new Date(release.published_at).toLocaleDateString('ru-RU')}`
      : 'версия не определена';
  }

  if (!release) return;

  // Подставляем прямые ссылки на файлы, чтобы «Скачать» вело сразу на установщик.
  const map = { win: 'win', 'mac-arm': 'mac-arm', 'mac-x64': 'mac-x64' };
  document.querySelectorAll('[data-asset]').forEach((link) => {
    const asset = findAsset(release.assets, map[link.dataset.asset]);
    if (!asset) return;
    link.href = asset.browser_download_url;
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

  if (el.ctaMain) {
    const win = findAsset(release.assets, 'win');
    if (win) el.ctaMain.href = win.browser_download_url;
  }
}

async function load() {
  if (el.repoLink) el.repoLink.href = FALLBACK.repo;
  if (el.releasesLink) el.releasesLink.href = FALLBACK.releases;

  try {
    const response = await fetch(API, { headers: { Accept: 'application/vnd.github+json' } });
    if (!response.ok) throw new Error(`GitHub ответил ${response.status}`);

    const best = pickRelease(await response.json());
    if (!best) throw new Error('Нет опубликованных релизов');

    paint(best.version, best.release);
  } catch (error) {
    // Честное поведение: не показываем выдуманную версию, а ведём на страницу релизов.
    console.warn('Не удалось получить релизы:', error.message);
    if (el.versionPill) el.versionPill.textContent = 'Сборки на GitHub Releases';
    if (el.buildVersion) el.buildVersion.textContent = 'версия не определена';
    document.querySelectorAll('[data-asset]').forEach((link) => {
      link.href = FALLBACK.releases;
      link.title = 'Откроется страница релизов на GitHub';
    });
    if (el.ctaMain) el.ctaMain.href = FALLBACK.releases;
  }
}

load();
