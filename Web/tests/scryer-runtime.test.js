const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const webRoot = path.resolve(__dirname, '..');

function loadCore(apiClient) {
    const listeners = new Map();
    const window = {
        ScryerRuntime153: { version: '153.12', modules: {}, registerModule(name, version) { this.modules[name] = version; } },
        ApiClient: apiClient,
        addEventListener(name, listener) { listeners.set(name, listener); },
        removeEventListener(name) { listeners.delete(name); },
        setInterval() { return 1; },
        clearInterval() {},
        setTimeout() { return 1; },
        clearTimeout() {},
        location: { origin: 'https://jellyfin.test', href: 'https://jellyfin.test/web/index.html', hash: '' }
    };
    const document = {
        body: {},
        querySelector() { return null; },
        querySelectorAll() { return []; }
    };
    vm.runInNewContext(fs.readFileSync(path.join(webRoot, 'scryer-core.js'), 'utf8'), {
        window, document, URL, Promise, MutationObserver: function () {}, history: { pushState() {} }
    });
    return window.Scryer;
}

test('lifecycle has one mount per feature and disposes owned listeners', () => {
    const Scryer = loadCore();
    const target = {
        listeners: new Map(),
        addEventListener(name, listener) { this.listeners.set(name, listener); },
        removeEventListener(name) { this.listeners.delete(name); }
    };
    let firstDisposed = 0;
    let secondDisposed = 0;
    Scryer.lifecycle.registerFeature('sample', (container, scope) => {
        scope.on(target, 'click', () => {});
        return () => { firstDisposed++; };
    });
    Scryer.lifecycle.mount('sample', {}, {});
    assert.equal(target.listeners.has('click'), true);
    Scryer.lifecycle.registerFeature('other', () => () => { secondDisposed++; });
    Scryer.lifecycle.mount('sample', {}, {});
    assert.equal(firstDisposed, 1);
    assert.equal(target.listeners.has('click'), true);
    Scryer.lifecycle.disposeAll();
    assert.equal(target.listeners.has('click'), false);
    assert.equal(firstDisposed, 2);
    assert.equal(secondDisposed, 0);
});

test('loader declares the deterministic RFC 153 module sequence', () => {
    const source = fs.readFileSync(path.join(webRoot, 'scryer-loader.js'), 'utf8');
    const modules = ['scryer-strings.js', 'scryer-core.js', 'scryer-styles.js', 'scryer-discovery.js', 'scryer-calendar.js', 'scryer-requests.js', 'scryer-downloads.js'];
    let previous = -1;
    modules.forEach((module) => {
        const next = source.indexOf(module);
        assert.ok(next > previous, module + ' must load after its predecessor');
        previous = next;
    });
    assert.match(source, /runtime\.startPromise/);
    assert.match(source, /assertReady/);
    assert.match(source, /loadScript\('scryer-downloads\.js', 'download',[\s\S]*?hasFeature\('download'\)/);
});

test('request choices and calendar grouping remain deterministic', () => {
    const Scryer = loadCore();
    const profiles = [{ id: 'one', name: 'One' }, { id: 'two', name: 'Two' }];
    assert.equal(JSON.stringify(Scryer.ui.profilesForLibrary(profiles, { requestQualityProfileIds: ['two'] })), JSON.stringify([{ id: 'two', name: 'Two' }]));
    assert.equal(JSON.stringify(Scryer.ui.groupByDate([{ airDate: '2026-09-02', id: 'b' }, { airDate: '2026-09-01', id: 'a' }, { airDate: '2026-09-02', id: 'c' }])), JSON.stringify([{ date: '2026-09-01', items: [{ airDate: '2026-09-01', id: 'a' }] }, { date: '2026-09-02', items: [{ airDate: '2026-09-02', id: 'b' }, { airDate: '2026-09-02', id: 'c' }] }]));
    assert.equal(Scryer.ui.monitorOptions.some((option) => option.value === 'MISSING_AND_FUTURE_EPISODES'), true);
});

test('Jellyfin DTO responses normalize to the browser field contract', () => {
    const Scryer = loadCore();
    const normalized = Scryer._testing.normalizeApiPayload({
        Configured: true,
        Connected: true,
        AccountLinked: false,
        capabilities: { ScryerUserId: 'user-c', Libraries: [{ LibraryId: 'view', CanView: true }] }
    });
    assert.equal(JSON.stringify(normalized), JSON.stringify({
        configured: true,
        connected: true,
        accountLinked: false,
        capabilities: { scryerUserId: 'user-c', libraries: [{ libraryId: 'view', canView: true }] }
    }));
});

test('plugin API calls stay on the web-shell origin so callback cookies are sent', () => {
    const Scryer = loadCore();
    const client = { getUrl(path) { return 'http://jellyfin.internal:8096/base/' + path; } };
    assert.equal(
        Scryer._testing.pluginApiUrl(client, 'Scryer/Auth/Finalize'),
        'https://jellyfin.test/base/Scryer/Auth/Finalize'
    );
});

test('relative Scryer image routes resolve from the public origin, not its OAuth path', async () => {
    const client = {
        getUrl(pathName) { return 'https://jellyfin.test/' + pathName; },
        serverAddress() { return 'https://jellyfin.test'; },
        getCurrentUserId() { return 'alice'; },
        getCurrentUser() { return Promise.resolve({ Id: 'alice' }); },
        ajax() {
            return Promise.resolve({
                ok: true,
                status: 200,
                text() { return Promise.resolve(JSON.stringify({ ImageBaseUrl: 'https://scryer.test/system/users' })); }
            });
        }
    };
    const Scryer = loadCore(client);
    assert.equal(
        await Scryer.resolveImageUrl('/images/media/poster-token/w250'),
        'https://scryer.test/images/media/poster-token/w250'
    );
});

test('recent watch seeds preserve Jellyfin history order and resolve episode parents', async () => {
    const calls = [];
    const client = {
        getUrl(pathName) { return 'https://jellyfin.test/' + pathName; },
        serverAddress() { return 'https://jellyfin.test'; },
        getCurrentUserId() { return 'alice'; },
        getCurrentUser() { return Promise.resolve({ Id: 'alice' }); },
        getItems(userId, options) {
            calls.push({ userId, options });
            if (calls.length === 1) return Promise.resolve({ Items: [
                { Type: 'Episode', SeriesId: 'series-1', SeriesName: 'First Show' },
                { Type: 'Episode', SeriesId: 'series-1', SeriesName: 'First Show' },
                { Type: 'Movie', Id: 'movie-1', Name: 'A Movie', ProviderIds: { Tmdb: '42' } },
                { Type: 'Episode', SeriesId: 'series-2', SeriesName: 'Second Show' }
            ] });
            return Promise.resolve({ Items: [
                { Id: 'series-1', Name: 'First Show', ProviderIds: { Tvdb: '100' } },
                { Id: 'series-2', Name: 'Second Show', ProviderIds: { Imdb: 'tt200' } }
            ] });
        }
    };
    const Scryer = loadCore(client);
    const seeds = await Scryer.getRecentWatchSeeds(3);
    assert.equal(JSON.stringify(seeds), JSON.stringify([
        { entityId: 'series-1', title: 'First Show', kind: 'SERIES', providerIds: { tvdb: '100' } },
        { entityId: 'movie-1', title: 'A Movie', kind: 'MOVIE', providerIds: { tmdb: '42' } },
        { entityId: 'series-2', title: 'Second Show', kind: 'SERIES', providerIds: { imdb: 'tt200' } }
    ]));
    assert.equal(calls[0].options.SortBy, 'DatePlayed');
    assert.equal(calls[0].options.IncludeItemTypes, 'Movie,Episode');
    assert.equal(calls[0].options.Filters, 'IsPlayed');
    assert.equal(calls[1].options.Ids, 'series-1,series-2');
});

test('connected states render feature content without status or disconnect banners', () => {
    const source = fs.readFileSync(path.join(webRoot, 'scryer-core.js'), 'utf8');
    assert.match(source, /status\.accountLinked === false/);
    assert.match(source, /kind === 'connected' \|\| kind === 'anonymous' \|\| kind === 'limited'/);
    assert.match(source, /container\.innerHTML = '<div class="scryerFeatureBody"><\/div>'/);
    assert.doesNotMatch(source, /Scryer connected\./);
    assert.doesNotMatch(source, /disconnect\.textContent/);
});

test('an unlinked configured user is prompted to connect', () => {
    const Scryer = loadCore();
    assert.equal(Scryer._testing.connectionStateForStatus({ configured: true, connected: false, accountLinked: false }), 'connect');
    assert.equal(Scryer._testing.connectionStateForStatus({ configured: false, connected: false }), 'unconfigured');
});

test('generation gates reject callbacks from a closed or replaced UI operation', () => {
    const Scryer = loadCore();
    const gate = Scryer.ui.createGenerationGate();
    const firstOpen = gate.begin();
    assert.equal(gate.isCurrent(firstOpen), true);
    gate.invalidate();
    assert.equal(gate.isCurrent(firstOpen), false);
    const secondOpen = gate.begin();
    assert.equal(gate.isCurrent(secondOpen), true);
    assert.equal(gate.isCurrent(firstOpen), false);
});

test('page capability gates distinguish discovery, requests, and view-only pages', () => {
    const Scryer = loadCore();
    assert.equal(Scryer.ui.hasPageCapability('discovery', [{ canView: false, canRequest: true, canManageTitles: false }]), true);
    assert.equal(Scryer.ui.hasPageCapability('discovery', [{ canView: false, canRequest: false, canManageTitles: true }]), true);
    assert.equal(Scryer.ui.hasPageCapability('requests', [{ canView: true, canRequest: false, canManageTitles: false }]), false);
    assert.equal(Scryer.ui.hasPageCapability('requests', [{ canView: false, canRequest: false, canManageTitles: true }]), true);
    assert.equal(Scryer.ui.hasPageCapability('calendar', [{ canView: false, canRequest: true, canManageTitles: true }]), false);
    assert.equal(Scryer.ui.hasPageCapability('download', [{ canView: true, canRequest: false, canManageTitles: false }]), true);
});
