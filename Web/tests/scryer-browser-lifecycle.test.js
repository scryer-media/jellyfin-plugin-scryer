const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const webRoot = path.resolve(__dirname, '..');

function readWebAsset(name) {
    return fs.readFileSync(path.join(webRoot, name), 'utf8');
}

function createCoreHarness(apiClient) {
    const listeners = new Map();
    const timeouts = new Map();
    const intervals = new Map();
    const diagnostics = [];
    let nextHandle = 1;
    const window = {
        ScryerRuntime153: { version: '153.13', modules: {}, registerModule(name, version) { this.modules[name] = version; } },
        ScryerStrings: { pages: {}, states: { requestConflict: 'This request conflicts with its current Scryer state.', internalError: 'The Scryer request could not be completed.' } },
        ApiClient: apiClient,
        addEventListener(name, listener) { listeners.set(name, listener); },
        removeEventListener(name) { listeners.delete(name); },
        setTimeout(callback) { const handle = nextHandle++; timeouts.set(handle, callback); return handle; },
        clearTimeout(handle) { timeouts.delete(handle); },
        setInterval(callback) { const handle = nextHandle++; intervals.set(handle, callback); return handle; },
        clearInterval(handle) { intervals.delete(handle); },
        location: { origin: 'https://jellyfin.test', href: 'https://jellyfin.test/web/index.html', hash: '' }
    };
    const document = {
        body: {},
        querySelector() { return null; },
        querySelectorAll() { return []; }
    };
    vm.runInNewContext(readWebAsset('scryer-core.js'), {
        window, document, URL, Promise, MutationObserver: function () {}, history: { pushState() {} },
        console: { error(...args) { diagnostics.push(args); } }
    });
    return { Scryer: window.Scryer, window, document, listeners, timeouts, intervals, diagnostics };
}

function createLibraryClient(userIdRef, calls) {
    return {
        getUrl(pathName) { return 'https://jellyfin.test/' + pathName; },
        serverAddress() { return 'https://jellyfin.test'; },
        getCurrentUserId() { return userIdRef.value; },
        getCurrentUser() { return Promise.resolve({ Id: userIdRef.value }); },
        ajax(request) {
            calls.push(request.url);
            return Promise.resolve({
                ok: true,
                status: 200,
                json() {},
                text() { return Promise.resolve(JSON.stringify({ libraries: [{ id: userIdRef.value }] })); }
            });
        }
    };
}

async function settle() {
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
}

test('rejected Jellyfin responses preserve the plugin error vocabulary', async () => {
    const client = {
        getUrl(pathName) { return 'https://jellyfin.test/' + pathName; },
        serverAddress() { return 'https://jellyfin.test'; },
        getCurrentUserId() { return 'alice'; },
        getCurrentUser() { return Promise.resolve({ Id: 'alice' }); },
        ajax() {
            return Promise.reject({
                ok: false,
                status: 409,
                text() { return Promise.resolve(JSON.stringify({ Code: 'request_conflict', Message: 'internal detail' })); }
            });
        }
    };
    const harness = createCoreHarness(client);
    await assert.rejects(harness.Scryer.apiGet('Scryer/Test'), (error) => {
        assert.equal(error.code, 'request_conflict');
        assert.equal(error.status, 409);
        assert.equal(error.message, 'This request conflicts with its current Scryer state.');
        return true;
    });
});

test('rejected Jellyfin responses never disclose unknown error fields', async () => {
    const responses = [
        { Code: 'upstream_secret_code', Message: 'secret-upstream-detail' },
        { Code: { Nested: 'secret-object-code' }, Message: 'second-secret-detail' }
    ];
    const client = {
        getUrl(pathName) { return 'https://jellyfin.test/' + pathName; },
        serverAddress() { return 'https://jellyfin.test'; },
        getCurrentUserId() { return 'alice'; },
        getCurrentUser() { return Promise.resolve({ Id: 'alice' }); },
        ajax() {
            const payload = responses.shift();
            return Promise.reject({
                ok: false,
                status: '500',
                text() { return Promise.resolve(JSON.stringify(payload)); }
            });
        }
    };
    const harness = createCoreHarness(client);
    for (let attempt = 0; attempt < 2; attempt++) {
        await assert.rejects(harness.Scryer.apiGet('Scryer/Test'), (error) => {
            assert.equal(error.code, 'internal_error');
            assert.equal(error.status, 0);
            assert.equal(error.message, 'The Scryer request could not be completed.');
            return true;
        });
    }
    const diagnostics = JSON.stringify(harness.diagnostics);
    assert.doesNotMatch(diagnostics, /upstream_secret_code|secret-upstream-detail|secret-object-code|second-secret-detail/);
    assert.equal(diagnostics, JSON.stringify([
        ['[Scryer] API request failed:', 'internal_error', 0],
        ['[Scryer] API request failed:', 'internal_error', 0]
    ]));
});

test('lifecycle navigation remounts are idempotent and clean every owned resource', () => {
    const harness = createCoreHarness(null);
    const lifecycle = harness.Scryer._testing.createLifecycle();
    const target = {
        listeners: new Map(),
        addEventListener(name, listener) { this.listeners.set(name, listener); },
        removeEventListener(name, listener) { assert.equal(this.listeners.get(name), listener); this.listeners.delete(name); }
    };
    let disposerCalls = 0;
    let callbackCalls = 0;
    lifecycle.registerFeature('page', (container, scope) => {
        scope.on(target, 'click', () => { callbackCalls++; });
        scope.timeout(() => { callbackCalls++; }, 50);
        scope.interval(() => { callbackCalls++; }, 100);
        return () => { disposerCalls++; };
    });

    lifecycle.mount('page', {}, {});
    assert.equal(target.listeners.size, 1);
    assert.equal(harness.timeouts.size, 1);
    assert.equal(harness.intervals.size, 1);
    lifecycle.mount('page', {}, {});
    assert.equal(disposerCalls, 1);
    assert.equal(target.listeners.size, 1);
    assert.equal(harness.timeouts.size, 1);
    assert.equal(harness.intervals.size, 1);

    lifecycle.disposeAll();
    assert.equal(disposerCalls, 2);
    assert.equal(target.listeners.size, 0);
    assert.equal(harness.timeouts.size, 0);
    assert.equal(harness.intervals.size, 0);
    assert.equal(callbackCalls, 0);
});

test('logout, login, and user switches cannot reuse a prior user cache', async () => {
    const user = { value: 'alice' };
    const calls = [];
    const client = createLibraryClient(user, calls);
    const harness = createCoreHarness(client);

    assert.equal(JSON.stringify(await harness.Scryer.getLibraries()), JSON.stringify([{ id: 'alice' }]));
    assert.equal(JSON.stringify(await harness.Scryer.getLibraries()), JSON.stringify([{ id: 'alice' }]));
    assert.equal(calls.length, 1, 'same-user calls use the in-memory cache');

    user.value = 'bob';
    assert.equal(JSON.stringify(await harness.Scryer.getLibraries()), JSON.stringify([{ id: 'bob' }]));
    assert.equal(calls.length, 2, 'a changed Jellyfin user gets a new cache key');

    harness.window.ApiClient = null;
    const unavailable = harness.Scryer.getLibraries();
    await settle();
    for (let attempt = 0; attempt < 201; attempt++) {
        for (const callback of [...harness.intervals.values()]) callback();
    }
    await assert.rejects(unavailable, /Jellyfin API client unavailable/);

    user.value = 'carol';
    harness.window.ApiClient = client;
    assert.equal(JSON.stringify(await harness.Scryer.getLibraries()), JSON.stringify([{ id: 'carol' }]));
    assert.equal(calls.length, 3, 'a post-login call cannot recover Bob\'s cached libraries');
});

function createDownloadsHarness(responses) {
    const registered = new Map();
    const scheduled = [];
    const cleared = [];
    const documentListeners = new Map();
    let nextTimer = 1;
    const document = { hidden: false };
    const window = {
        setTimeout(callback, delay) {
            const timer = { id: nextTimer++, callback, delay, cleared: false };
            scheduled.push(timer);
            return timer.id;
        },
        clearTimeout(id) {
            const timer = scheduled.find((entry) => entry.id === id);
            if (timer) timer.cleared = true;
            cleared.push(id);
        }
    };
    const apiCalls = [];
    const Scryer = {
        apiGet(pathName) {
            apiCalls.push(pathName);
            const response = responses.shift();
            return response instanceof Error ? Promise.reject(response) : Promise.resolve(response);
        },
        escapeHtml(value) { return String(value); },
        LOADING_HTML: '<p>Loading</p>',
        lifecycle: { registerFeature(name, mount) { registered.set(name, mount); } },
        withConnectionGate(container, scope, page, render) { return render(container, scope, page); }
    };
    vm.runInNewContext(readWebAsset('scryer-downloads.js'), { window: { Scryer, setTimeout: window.setTimeout, clearTimeout: window.clearTimeout }, document, Date, Math, Promise });

    const list = { innerHTML: '' };
    const activeButton = { dataset: { tab: 'active' }, classList: { toggle() {} } };
    const historyButton = { dataset: { tab: 'history' }, classList: { toggle() {} } };
    const container = {
        innerHTML: '',
        querySelector(selector) { return selector === '.scryerDownloadList' ? list : null; },
        querySelectorAll(selector) { return selector === '.scryerTab' ? [activeButton, historyButton] : []; }
    };
    const owned = [];
    const scope = {
        isCurrent() { return true; },
        guard(callback) { return function () { return callback.apply(null, arguments); }; },
        own(callback) { owned.push(callback); return callback; },
        on(target, eventName, listener) {
            if (target === document) documentListeners.set(eventName, listener);
            else target.listener = listener;
            return this.own(() => {});
        }
    };
    return { registered, scheduled, cleared, document, documentListeners, container, scope, apiCalls, activeButton, historyButton, owned };
}

test('download polling backs off, pauses while hidden, and does not poll history', async () => {
    const harness = createDownloadsHarness([
        new Error('first failure'),
        new Error('second failure'),
        { downloadQueuePage: { items: [] } },
        { downloadHistory: { items: [] } }
    ]);
    harness.registered.get('download')(harness.container, harness.scope, { page: { id: 'download' } });
    await settle();
    assert.deepEqual(harness.scheduled.map((timer) => timer.delay), [10000]);

    await harness.scheduled[0].callback();
    await settle();
    assert.deepEqual(harness.scheduled.map((timer) => timer.delay), [10000, 20000]);

    harness.document.hidden = true;
    harness.documentListeners.get('visibilitychange')();
    assert.equal(harness.scheduled[1].cleared, true, 'hiding the document cancels the outstanding poll');

    harness.document.hidden = false;
    harness.documentListeners.get('visibilitychange')();
    await settle();
    assert.deepEqual(harness.scheduled.map((timer) => timer.delay), [10000, 20000, 5000]);

    harness.container.listener({ target: { closest() { return harness.historyButton; } } });
    await settle();
    assert.deepEqual(harness.apiCalls, ['Scryer/Downloads', 'Scryer/Downloads', 'Scryer/Downloads', 'Scryer/Downloads/History']);
    assert.equal(harness.scheduled[2].cleared, true, 'switching to history cancels active-download polling');
    assert.equal(harness.scheduled.length, 3, 'history does not schedule a polling timer');
});

test('modal keyboard/focus and stale-operation protections remain part of the discovery contract', () => {
    const source = readWebAsset('scryer-discovery.js');
    assert.match(source, /var modalGate = Scryer\.ui\.createGenerationGate\(\);/);
    assert.match(source, /scope\.own\(function \(\) \{ modalGate\.invalidate\(\); \}\);/);
    assert.match(source, /function closeModal\(\) \{[\s\S]*?modalGate\.invalidate\(\);[\s\S]*?lastFocused\.focus\(\);/);
    assert.match(source, /if \(event\.key === 'Escape'\) \{ event\.preventDefault\(\); closeModal\(\); return; \}/);
    assert.match(source, /if \(event\.shiftKey && document\.activeElement === first\)[\s\S]*?last\.focus\(\);/);
    assert.match(source, /if \(!event\.shiftKey && document\.activeElement === last\)[\s\S]*?first\.focus\(\);/);
    assert.match(source, /var isCurrentModal = function \(\) \{ return scope\.isCurrent\(\) && modalGate\.isCurrent\(modalToken\); \};/);
    assert.match(source, /if \(!isCurrentModal\(\)\) return;/);
});

test('disabled feature navigation, API capability gates, and browser credential boundaries stay explicit', () => {
    const core = readWebAsset('scryer-core.js');
    const loader = readWebAsset('scryer-loader.js');
    const runtimeAssets = ['scryer-loader.js', 'scryer-core.js', 'scryer-discovery.js', 'scryer-calendar.js', 'scryer-requests.js', 'scryer-downloads.js'];

    assert.match(core, /PAGES = PAGE_DEFINITIONS\.filter\(function \(page\) \{ return features\[page\.feature\] === true; \}\);/);
    assert.match(core, /if \(pageId === 'discovery'\) return library\.canView \|\| library\.canRequest \|\| library\.canManageTitles;/);
    assert.match(core, /if \(pageId === 'requests'\) return library\.canRequest \|\| library\.canManageTitles;/);
    assert.match(core, /if \(pageId === 'calendar' \|\| pageId === 'download'\) return library\.canView;/);
    assert.match(core, /event\.stopImmediatePropagation\(\);[\s\S]*?showPage\(page\.id\);[\s\S]*?\}, true\);/);
    assert.match(core, /<svg class="navMenuOptionIcon scryerNavIcon"/);
    assert.doesNotMatch(core, /<span class="material-icons navMenuOptionIcon"/);
    assert.doesNotMatch(readWebAsset('scryer-discovery.js'), /class="material-icons"/);
    assert.doesNotMatch(readWebAsset('scryer-calendar.js'), /class="material-icons"/);
    ['discovery', 'calendar', 'requests', 'downloads'].forEach((feature) => assert.match(loader, new RegExp("loadScript\\('scryer-" + feature + "\\.js'")));

    const combined = runtimeAssets.map(readWebAsset).join('\n');
    assert.doesNotMatch(combined, /\b(?:localStorage|indexedDB)\b/);
    assert.match(core, /sessionStorage\.(?:setItem|removeItem)\(OAUTH_POPUP_MARKER/);
    assert.doesNotMatch(combined, /document\.cookie\b/);
    assert.doesNotMatch(combined, /\bAuthorization\s*:/);
    assert.doesNotMatch(combined, /\bBearer\s+/i);
});

test('injected and loaded web assets share one cache version', () => {
    const loader = readWebAsset('scryer-loader.js');
    const core = readWebAsset('scryer-core.js');
    const injector = fs.readFileSync(path.resolve(webRoot, '..', 'WebInjection', 'ScriptTagInjectionStartupFilter.cs'), 'utf8');
    const version = loader.match(/var VERSION = '([^']+)'/)[1];

    assert.match(core, new RegExp("var VERSION = '" + version.replace('.', '\\.') + "'"));
    assert.equal(injector.includes('scryer-loader.js?v=' + version + '\\" data-scryer-loader=\\"' + version), true);
});

test('custom pages use Jellyfin library page spacing below the fixed header', () => {
    const core = readWebAsset('scryer-core.js');
    const styles = readWebAsset('scryer-styles.js');

    assert.match(core, /root\.className = 'page type-interior libraryPage mainAnimatedPage hide scryer-runtime-owned';/);
    assert.doesNotMatch(core, /querySelector\('\.pageTitle'\)/);
    assert.match(core, /classList\.add\('scryerPageActive'\)/);
    assert.match(core, /classList\.remove\('scryerPageActive'\)/);
    assert.match(styles, /\.scryerPageActive \.pageTitle\{display:none\}/);
    assert.equal(styles.includes('.scryerPageActive .skinHeader .material-icons.arrow_back:before{content:"\\\\e5c4"!important}'), true);
    assert.equal(styles.includes('.scryerPageActive .skinHeader .material-icons.menu:before{content:"\\\\e5d2"!important}'), true);
    assert.equal(styles.includes('.scryerPageActive .skinHeader .material-icons.search:before{content:"\\\\e8b6"!important}'), true);
    assert.doesNotMatch(styles, /[\uE000-\uF8FF]/);
});

test('discovery prepends at most five watch-history recommendation rails', () => {
    const discovery = readWebAsset('scryer-discovery.js');

    assert.match(discovery, /Scryer\.getRecentWatchSeeds\(5\)/);
    assert.match(discovery, /Scryer\/Discovery\/MoreLikeThis\?source=/);
    assert.match(discovery, /groups\.filter\(function \(group\) \{ return !!group; \}\)\.slice\(0, 5\)/);
    assert.match(discovery, /results\[0\]\.concat\(results\[1\]\.groups\)/);
});

test('discovery offers direct catalog adds only through manageable libraries', () => {
    const discovery = readWebAsset('scryer-discovery.js');

    assert.match(discovery, /Scryer\/Libraries\/Manageable\?facet=/);
    assert.match(discovery, /apiPost\('Scryer\/Catalog\/Titles'/);
    assert.match(discovery, /selected && selected\._canManageTitles \? 'Add to Scryer' : 'Request'/);
    assert.match(discovery, /if \(selected && selected\._canManageTitles\) submitAdd/);
    assert.match(discovery, /else submitRequest/);
});

test('discovery cards render as a clean poster wall without button slabs', () => {
    const styles = readWebAsset('scryer-styles.js');
    const requests = readWebAsset('scryer-requests.js');

    assert.match(styles, /\.scryerCard\{[^}]*background:transparent!important/);
    assert.match(styles, /\.scryerCardPoster\{[^}]*aspect-ratio:2\/3/);
    assert.match(styles, /\.scryerCard:hover \.scryerCardPoster/);
    assert.equal((styles.match(/'\.scryerRow\{/g) || []).length, 1);
    assert.match(styles, /\.scryerRequestRow\{/);
    assert.match(requests, /row\.className = 'scryerRequestRow'/);
});

test('calendar retries missing episode artwork with the parent title poster', () => {
    const calendar = readWebAsset('scryer-calendar.js');

    assert.match(calendar, /var urls = \[episodeUrl, titleUrl\]/);
    assert.match(calendar, /image\.onerror = scope\.guard\(function \(\) \{ tryImage\(index \+ 1\); \}\)/);
    assert.match(calendar, /renderPosterInto\(card\.querySelector\('\.scryerCalendarPoster'\), item\.imageUrl, titlePosters\[item\.titleId\], scope\)/);
    assert.doesNotMatch(calendar, /titlePosters\[item\.titleId\] \|\| item\.imageUrl/);
});

test('OAuth finalization failures remain visible instead of falling back to Connect', () => {
    const core = readWebAsset('scryer-core.js');

    assert.match(core, /state\.finalizeFailure = error/);
    assert.match(core, /finalizeFailure \? codeToConnectionState\(finalizeFailure\.code\) : connectionState/);
    assert.match(core, /Scryer\/Auth\/Finalize'\)\.catch\(function \(\) \{ return null; \}\)/);
});

test('OAuth uses a centered 800 by 700 popup and refreshes its opener', () => {
    const core = readWebAsset('scryer-core.js');

    assert.match(core, /var width = 800;/);
    assert.match(core, /var height = 700;/);
    assert.match(core, /window\.open\('', OAUTH_WINDOW_NAME,/);
    assert.match(core, /popup\.location\.replace\(data\.authorizationUrl\)/);
    assert.match(core, /window\.opener\.postMessage\(\{/);
    assert.match(core, /window\.close\(\);/);
    assert.match(core, /pollOAuthCompletion\(popup, OAUTH_POLL_LIMIT\)/);
    assert.match(core, /if \(status && status\.connected\) \{/);
    assert.match(core, /if \(error && !openerAvailable\) return false;/);
    assert.match(core, /event\.source !== state\.oauthWindow/);
    assert.match(core, /refreshVisiblePage\(\);/);
});

test('OAuth completion is finalized and detected by the opener without postMessage', async () => {
    const calls = [];
    const client = {
        getUrl(pathName) { return 'https://jellyfin.test/' + pathName; },
        serverAddress() { return 'https://jellyfin.test'; },
        getCurrentUserId() { return 'alice'; },
        getCurrentUser() { return Promise.resolve({ Id: 'alice' }); },
        ajax(request) {
            calls.push(request.url);
            let payload = null;
            if (request.url.endsWith('/Scryer/Auth/Start')) payload = { AuthorizationUrl: 'https://scryer.test/oauth/authorize' };
            if (request.url.endsWith('/Scryer/Auth/Status')) payload = { Configured: true, Connected: true, AccountLinked: true };
            return Promise.resolve({
                ok: true,
                status: payload ? 200 : 204,
                text() { return Promise.resolve(payload ? JSON.stringify(payload) : ''); }
            });
        }
    };
    const harness = createCoreHarness(client);
    const popupStorage = new Map();
    const popup = {
        closed: false,
        focus() {},
        close() { this.closed = true; },
        location: { replace(url) { this.url = url; } },
        sessionStorage: {
            setItem(key, value) { popupStorage.set(key, value); },
            removeItem(key) { popupStorage.delete(key); }
        }
    };
    harness.window.open = () => popup;

    await harness.Scryer.startConnection('#/scryer-discovery');
    for (let attempt = 0; attempt < 20 && calls.length < 3; attempt++) await Promise.resolve();
    await new Promise((resolve) => setImmediate(resolve));

    assert.equal(JSON.stringify(calls), JSON.stringify([
        'https://jellyfin.test/Scryer/Auth/Start',
        'https://jellyfin.test/Scryer/Auth/Finalize',
        'https://jellyfin.test/Scryer/Auth/Status'
    ]));
    assert.equal(popup.location.url, 'https://scryer.test/oauth/authorize');
    assert.equal(popupStorage.get('scryer-oauth-popup'), '1');
    assert.equal(popup.closed, true);
    assert.equal(harness.timeouts.size, 0);
});

test('account connection uses the centered SVG brand card', () => {
    const core = readWebAsset('scryer-core.js');
    const styles = readWebAsset('scryer-styles.js');

    assert.match(core, /class="scryerConnectCard"/);
    assert.match(core, /src="\/Scryer\/Web\/scryer-logo\.svg"/);
    assert.match(core, /class="scryerConnectArrows"/);
    assert.match(core, /src="\/Scryer\/Web\/jellyfin-logo\.svg" alt="Jellyfin"/);
    assert.match(readWebAsset('jellyfin-logo.svg'), /viewBox="0 0 512 512"/);
    assert.match(core, /scryerConnectButton/);
    assert.match(styles, /\.scryerConnectCard\{[^}]*align-items:center/);
    assert.match(styles, /\.scryerConnectButton\{[^}]*linear-gradient/);
});
