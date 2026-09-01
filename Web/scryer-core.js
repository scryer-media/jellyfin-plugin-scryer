/* RFC 153 browser runtime: user-scoped API helpers, lifecycle, navigation, and connection UI. */
(function () {
    'use strict';

    var VERSION = '153.1';
    var runtime = window.ScryerRuntime153;
    if (!runtime || runtime.version !== VERSION) throw new Error('Scryer loader must run before core.');
    if (window.Scryer && window.Scryer.version === VERSION && window.Scryer._rfc153Installed) {
        runtime.registerModule('core', VERSION);
        return;
    }

    var Scryer = window.Scryer = {};
    var Strings = window.ScryerStrings || { pages: {}, states: {} };
    Scryer.version = VERSION;
    Scryer._rfc153Installed = true;
    Scryer.modules = {};

    var PAGE_DEFINITIONS = [
        { id: 'discovery', feature: 'discovery', title: Strings.pages.discovery || 'Discover', icon: 'explore', route: '#/scryer-discovery' },
        { id: 'calendar', feature: 'calendar', title: Strings.pages.calendar || 'Calendar', icon: 'event', route: '#/scryer-calendar' },
        { id: 'requests', feature: 'requests', title: Strings.pages.requests || 'Requests', icon: 'playlist_add_check', route: '#/scryer-requests' },
        { id: 'download', feature: 'downloads', title: Strings.pages.downloads || 'Downloads', icon: 'download', route: '#/scryer-download' }
    ];
    var PAGES = PAGE_DEFINITIONS.slice();
    Scryer.PAGES = PAGES;
    Scryer.LOADING_HTML = '<div class="scryerLoading" role="status" aria-live="polite"><div class="scryerSpinner"></div></div>';

    function escapeHtml(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, function (character) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character];
        });
    }
    Scryer.escapeHtml = escapeHtml;

    function createLifecycle() {
        var features = {};
        var mounts = {};
        var generation = 0;

        function createScope(id, mountGeneration) {
            var alive = true;
            var cleanup = [];
            function track(disposer) {
                if (typeof disposer === 'function') cleanup.push(disposer);
                return disposer;
            }
            return {
                id: id,
                generation: mountGeneration,
                isCurrent: function () { return alive && generation === mountGeneration; },
                guard: function (callback) {
                    return function () {
                        if (!alive || generation !== mountGeneration) return undefined;
                        return callback.apply(null, arguments);
                    };
                },
                on: function (target, eventName, listener, options) {
                    if (!target || !target.addEventListener) return function () {};
                    target.addEventListener(eventName, listener, options);
                    return track(function () { target.removeEventListener(eventName, listener, options); });
                },
                timeout: function (callback, milliseconds) {
                    var handle = window.setTimeout(this.guard(callback), milliseconds);
                    return track(function () { window.clearTimeout(handle); });
                },
                interval: function (callback, milliseconds) {
                    var handle = window.setInterval(this.guard(callback), milliseconds);
                    return track(function () { window.clearInterval(handle); });
                },
                own: track,
                dispose: function () {
                    if (!alive) return;
                    alive = false;
                    while (cleanup.length) {
                        try { cleanup.pop()(); } catch (error) {}
                    }
                }
            };
        }

        function unmount(id) {
            var current = mounts[id];
            if (!current) return;
            delete mounts[id];
            current.scope.dispose();
            if (typeof current.disposer === 'function') {
                try { current.disposer(); } catch (error) {}
            }
        }

        return {
            registerFeature: function (id, mount) {
                if (!id || typeof mount !== 'function') throw new Error('Scryer feature registration is invalid.');
                features[id] = mount;
                Scryer.modules[id] = VERSION;
                runtime.registerModule(id, VERSION);
            },
            hasFeature: function (id) { return typeof features[id] === 'function'; },
            mount: function (id, container, context) {
                unmount(id);
                if (!features[id]) throw new Error('Scryer feature is unavailable: ' + id + '.');
                generation++;
                var scope = createScope(id, generation);
                var disposer = features[id](container, scope, context);
                mounts[id] = { scope: scope, disposer: disposer };
                return scope;
            },
            unmount: unmount,
            disposeAll: function () {
                Object.keys(mounts).forEach(unmount);
                generation++;
            },
            generation: function () { return generation; }
        };
    }
    Scryer.lifecycle = createLifecycle();
    Scryer._testing = { createLifecycle: createLifecycle };

    var state = {
        visibleId: null, previousPage: null, featureConfigReady: false, featureConfigKey: null,
        started: false, disposed: false, navObserver: null, reconcileTimer: null, apiReadyTimer: null
    };

    function pageById(id) {
        for (var index = 0; index < PAGES.length; index++) if (PAGES[index].id === id) return PAGES[index];
        return null;
    }
    function pageByRoute(hash) {
        for (var index = 0; index < PAGES.length; index++) if (PAGES[index].route === hash) return PAGES[index];
        return null;
    }
    function isApiClientConnected(client) { return !!(client && client.getUrl && client.serverAddress && client.serverAddress()); }

    var apiClientReadyPromise = null;
    function whenApiClientReady() {
        if (isApiClientConnected(window.ApiClient)) return Promise.resolve(window.ApiClient);
        if (!apiClientReadyPromise) {
            apiClientReadyPromise = new Promise(function (resolve) {
                var attempts = 0;
                state.apiReadyTimer = window.setInterval(function () {
                    attempts++;
                    if (isApiClientConnected(window.ApiClient) || attempts > 200) {
                        window.clearInterval(state.apiReadyTimer);
                        state.apiReadyTimer = null;
                        apiClientReadyPromise = null;
                        resolve(isApiClientConnected(window.ApiClient) ? window.ApiClient : null);
                    }
                }, 100);
            });
        }
        return apiClientReadyPromise;
    }
    Scryer.whenApiClientReady = whenApiClientReady;

    var activeJellyfinContextKey = null;
    var jellyfinContextGeneration = 0;
    var activeJellyfinClient = null;
    var activeJellyfinServer = null;
    var activeJellyfinUserId = null;
    var webConfigPromise = null;
    var webConfigKey = null;
    var librariesCache = null;
    var librariesCacheKey = null;
    var librariesInFlight = null;
    var capabilitiesCache = null;
    var capabilitiesCacheKey = null;
    var capabilitiesInFlight = null;
    var qualityProfilesCache = null;
    var qualityProfilesCacheKey = null;
    var qualityProfilesInFlight = null;

    function canonicalServerAddress(client) {
        var raw = client && client.serverAddress ? client.serverAddress() : window.location.origin;
        try {
            var parsed = new URL(raw || window.location.href, window.location.href);
            return parsed.origin + parsed.pathname.replace(/\/+$/, '');
        } catch (error) { return window.location.origin; }
    }
    function canonicalUserId(value) { return value === undefined || value === null || value === '' ? null : String(value); }
    function readJellyfinIdentity(client) {
        return { client: client, server: canonicalServerAddress(client), userId: canonicalUserId(client && client.getCurrentUserId ? client.getCurrentUserId() : null) };
    }
    function sameJellyfinIdentity(left, right) {
        return !!(left && right && left.client === right.client && left.server === right.server && left.userId === right.userId);
    }

    function resetUserScopedState() {
        webConfigPromise = null;
        webConfigKey = null;
        librariesCache = null;
        librariesCacheKey = null;
        librariesInFlight = null;
        capabilitiesCache = null;
        capabilitiesCacheKey = null;
        capabilitiesInFlight = null;
        qualityProfilesCache = null;
        qualityProfilesCacheKey = null;
        qualityProfilesInFlight = null;
        state.featureConfigReady = false;
        state.featureConfigKey = null;
        PAGES = PAGE_DEFINITIONS.slice();
        Scryer.PAGES = PAGES;
        Scryer.lifecycle.disposeAll();
        document.querySelectorAll('.scryerSection, [id^="scryer-page-"], .scryer-runtime-owned').forEach(function (element) { element.remove(); });
        state.visibleId = null;
        state.previousPage = null;
    }
    function activateJellyfinIdentity(identity) {
        if (activeJellyfinClient === identity.client && activeJellyfinServer === identity.server && activeJellyfinUserId === identity.userId) return;
        jellyfinContextGeneration++;
        activeJellyfinContextKey = null;
        activeJellyfinClient = identity.client;
        activeJellyfinServer = identity.server;
        activeJellyfinUserId = identity.userId;
        resetUserScopedState();
    }
    function getCurrentJellyfinContext() {
        return whenApiClientReady().then(function (client) {
            if (!client) {
                jellyfinContextGeneration++;
                activeJellyfinContextKey = null;
                activeJellyfinClient = null;
                activeJellyfinServer = null;
                activeJellyfinUserId = null;
                resetUserScopedState();
                throw new Error('Jellyfin API client unavailable');
            }
            var identity = readJellyfinIdentity(client);
            activateJellyfinIdentity(identity);
            var capturedGeneration = jellyfinContextGeneration;
            return Promise.resolve(client.getCurrentUser ? client.getCurrentUser() : null).then(function (user) {
                var currentIdentity = readJellyfinIdentity(client);
                var responseUserId = canonicalUserId(user && (user.Id || user.id));
                if (capturedGeneration !== jellyfinContextGeneration || client !== window.ApiClient || !sameJellyfinIdentity(identity, currentIdentity) || responseUserId !== identity.userId) {
                    if (client === window.ApiClient && !sameJellyfinIdentity(identity, currentIdentity)) activateJellyfinIdentity(currentIdentity);
                    throw new Error('The active Jellyfin context changed.');
                }
                var key = identity.server + '\u001f' + (identity.userId || 'anonymous') + '\u001f' + jellyfinContextGeneration;
                activeJellyfinContextKey = key;
                return { client: client, user: user, key: key, generation: jellyfinContextGeneration, server: identity.server, userId: identity.userId };
            });
        });
    }
    function requireCurrentContext(context, value) {
        var identity = readJellyfinIdentity(context.client);
        if (context.client === window.ApiClient && !sameJellyfinIdentity({ client: context.client, server: context.server, userId: context.userId }, identity)) activateJellyfinIdentity(identity);
        if (activeJellyfinContextKey !== context.key || context.generation !== jellyfinContextGeneration || context.client !== window.ApiClient || context.server !== identity.server || context.userId !== identity.userId) {
            var error = new Error('The active Jellyfin user or server changed.');
            error.code = 'context_changed';
            throw error;
        }
        return value;
    }

    var FAILURE_STRING_KEYS = {
        not_configured: 'notConfigured', not_connected: 'notConnected', authorization_expired: 'authorizationExpired',
        permission_denied: 'permissionDenied', scryer_offline: 'offline', scryer_incompatible: 'incompatible',
        rate_limited: 'rateLimited', invalid_response: 'invalidResponse', request_conflict: 'requestConflict', internal_error: 'internalError'
    };
    function failureMessage(code, fallback) {
        var key = FAILURE_STRING_KEYS[code];
        return (key && Strings.states && Strings.states[key]) || fallback || (Strings.states && Strings.states.internalError) || 'The Scryer request could not be completed.';
    }
    Scryer.failureMessage = failureMessage;

    function apiCallForClient(client, method, path, body) {
        return client.ajax({ type: method, url: client.getUrl(path), data: body ? JSON.stringify(body) : undefined, contentType: body ? 'application/json' : undefined }).then(function (response) {
            if (!response || typeof response.json !== 'function') return response;
            return response.text().then(function (text) {
                var payload = null;
                if (text) try { payload = JSON.parse(text); } catch (error) {}
                if (!response.ok) {
                    var failure = new Error(failureMessage(payload && payload.code, payload && payload.message));
                    failure.code = (payload && payload.code) || 'internal_error';
                    failure.status = response.status;
                    throw failure;
                }
                return payload;
            });
        });
    }
    function apiCall(method, path, body) {
        return getCurrentJellyfinContext().then(function (context) {
            if (!context.user) throw new Error('No authenticated Jellyfin user is active.');
            return apiCallForClient(context.client, method, path, body).then(function (data) { return requireCurrentContext(context, data); });
        });
    }
    function apiGet(path) { return apiCall('GET', path); }
    function apiPost(path, body) { return apiCall('POST', path, body); }
    function apiPut(path, body) { return apiCall('PUT', path, body); }
    Scryer.apiGet = apiGet;
    Scryer.apiPost = apiPost;
    Scryer.apiPut = apiPut;

    function getWebConfiguration() {
        return getCurrentJellyfinContext().then(function (context) {
            if (!webConfigPromise || webConfigKey !== context.key) {
                webConfigKey = context.key;
                webConfigPromise = apiCallForClient(context.client, 'GET', 'Scryer/Web/config').then(function (data) { return requireCurrentContext(context, data); }, function (error) {
                    if (webConfigKey === context.key) webConfigPromise = null;
                    throw error;
                });
            }
            return webConfigPromise;
        });
    }
    function resolveImageUrl(url) {
        if (!url || !url.startsWith('/')) return Promise.resolve(url);
        return getWebConfiguration().then(function (data) {
            var base = data.imageBaseUrl || '';
            return base ? base.replace(/\/$/, '') + url : url;
        }).catch(function () { return url; });
    }
    Scryer.resolveImageUrl = resolveImageUrl;
    function getConnectionStatus() { return apiGet('Scryer/Auth/Status'); }
    Scryer.getConnectionStatus = getConnectionStatus;
    function startConnection(returnPage) {
        var page = pageByRoute(returnPage || window.location.hash);
        var target = page ? page.route : '#/scryer-discovery';
        return apiPost('Scryer/Auth/Start', { returnPage: target }).then(function (data) {
            if (!data || typeof data.authorizationUrl !== 'string' || data.authorizationUrl.length > 4096) throw new Error('Scryer returned an invalid authorization redirect.');
            window.location.assign(data.authorizationUrl);
        });
    }
    Scryer.startConnection = startConnection;
    Scryer.disconnect = function () { return apiPost('Scryer/Auth/Disconnect'); };

    function getLibraries() {
        return getCurrentJellyfinContext().then(function (context) {
            if (librariesCacheKey !== context.key) { librariesCacheKey = context.key; librariesCache = null; librariesInFlight = null; }
            if (librariesCache) return librariesCache;
            if (!librariesInFlight) librariesInFlight = apiCallForClient(context.client, 'GET', 'Scryer/Libraries').then(function (data) {
                var libraries = data.libraries || [];
                requireCurrentContext(context, libraries);
                if (librariesCacheKey === context.key) librariesCache = libraries;
                return libraries;
            }, function (error) { if (librariesCacheKey === context.key) librariesInFlight = null; throw error; });
            return librariesInFlight;
        });
    }
    Scryer.getLibraries = getLibraries;
    function getCapabilities() {
        return getCurrentJellyfinContext().then(function (context) {
            if (capabilitiesCacheKey !== context.key) { capabilitiesCacheKey = context.key; capabilitiesCache = null; capabilitiesInFlight = null; }
            if (capabilitiesCache) return capabilitiesCache;
            if (!capabilitiesInFlight) capabilitiesInFlight = apiCallForClient(context.client, 'GET', 'Scryer/Capabilities').then(function (data) {
                var capabilities = data && data.capabilities;
                if (!capabilities || !Array.isArray(capabilities.libraries)) throw new Error('Scryer returned invalid capabilities.');
                requireCurrentContext(context, capabilities);
                if (capabilitiesCacheKey === context.key) capabilitiesCache = capabilities;
                return capabilities;
            }, function (error) { if (capabilitiesCacheKey === context.key) capabilitiesInFlight = null; throw error; });
            return capabilitiesInFlight;
        });
    }
    Scryer.getCapabilities = getCapabilities;
    function getQualityProfiles() {
        return getCurrentJellyfinContext().then(function (context) {
            if (qualityProfilesCacheKey !== context.key) { qualityProfilesCacheKey = context.key; qualityProfilesCache = null; qualityProfilesInFlight = null; }
            if (qualityProfilesCache) return qualityProfilesCache;
            if (!qualityProfilesInFlight) qualityProfilesInFlight = apiCallForClient(context.client, 'GET', 'Scryer/Catalog/QualityProfiles').then(function (data) {
                var profiles = (data.qualityProfileSettings && data.qualityProfileSettings.profiles) || [];
                requireCurrentContext(context, profiles);
                if (qualityProfilesCacheKey === context.key) qualityProfilesCache = profiles;
                return profiles;
            }, function (error) { if (qualityProfilesCacheKey === context.key) qualityProfilesInFlight = null; throw error; });
            return qualityProfilesInFlight;
        });
    }
    Scryer.getQualityProfiles = getQualityProfiles;
    Scryer.facetOf = function (item) { var raw = (item.targetKind || item.facet || 'MOVIE').toUpperCase(); return raw === 'SERIES' || raw === 'ANIME' ? raw : 'MOVIE'; };
    Scryer.populateSelect = function (select, options, valueKey, labelKey, selectedValue) {
        select.innerHTML = options.map(function (option) {
            var value = option[valueKey];
            return '<option value="' + escapeHtml(value) + '"' + (value === selectedValue ? ' selected' : '') + '>' + escapeHtml(option[labelKey]) + '</option>';
        }).join('');
    };
    function hasPageCapability(pageId, libraries) {
        return (libraries || []).some(function (library) {
            if (pageId === 'discovery') return library.canView || library.canRequest;
            if (pageId === 'requests') return library.canRequest || library.canManageTitles;
            if (pageId === 'calendar' || pageId === 'download') return library.canView;
            return false;
        });
    }
    Scryer.ui = {
        monitorOptions: [
            { value: 'MONITORED', label: 'Monitor' },
            { value: 'UNMONITORED', label: 'Do not monitor' },
            { value: 'FUTURE_EPISODES', label: 'Future episodes' },
            { value: 'MISSING_AND_FUTURE_EPISODES', label: 'Missing and future episodes' },
            { value: 'ALL_EPISODES', label: 'All episodes' },
            { value: 'NONE', label: 'None' }
        ],
        profilesForLibrary: function (profiles, library) {
            var allowed = library && Array.isArray(library.requestQualityProfileIds) ? library.requestQualityProfileIds : [];
            if (!allowed.length && library && library.qualityProfileId) allowed = [library.qualityProfileId];
            return (profiles || []).filter(function (profile) { return allowed.indexOf(profile.id) >= 0; });
        },
        groupByDate: function (items) {
            var groups = {};
            (items || []).forEach(function (item) {
                var date = item && item.airDate ? item.airDate : 'unknown';
                if (!groups[date]) groups[date] = [];
                groups[date].push(item);
            });
            return Object.keys(groups).sort().map(function (date) { return { date: date, items: groups[date] }; });
        },
        createGenerationGate: function () {
            var generation = 0;
            return {
                begin: function () { generation++; return generation; },
                invalidate: function () { generation++; },
                isCurrent: function (token) { return token === generation; }
            };
        },
        hasPageCapability: hasPageCapability
    };

    function renderConnectionState(container, kind, scope, page, detail) {
        var messages = {
            unconfigured: Strings.states.notConfigured || 'Scryer is not configured.', connect: Strings.states.notConnected || 'Connect your Scryer account to continue.',
            connecting: 'Connecting Scryer…', connected: 'Scryer connected.', limited: 'Scryer connected with limited permissions.',
            expired: Strings.states.authorizationExpired || 'Your Scryer connection expired. Connect again.', offline: Strings.states.offline || 'Scryer is currently unreachable.',
            incompatible: Strings.states.incompatible || 'This Scryer server is incompatible.'
        };
        container.innerHTML = '<div class="scryerBanner scryerBannerWarning" role="status" aria-live="polite"><span>' + escapeHtml(detail || messages[kind] || messages.offline) + '</span><span class="scryerConnectionActions"></span></div><div class="scryerFeatureBody"></div>';
        var actions = container.querySelector('.scryerConnectionActions');
        if (kind === 'connect' || kind === 'expired') {
            var connect = document.createElement('button');
            connect.type = 'button'; connect.className = 'raised'; connect.textContent = 'Connect Scryer';
            scope.on(connect, 'click', function () {
                connect.disabled = true;
                renderConnectionState(container, 'connecting', scope, page);
                startConnection(page.route).catch(scope.guard(function (error) { renderConnectionState(container, 'offline', scope, page, error.message); }));
            });
            actions.appendChild(connect);
        } else if (kind === 'connected' || kind === 'limited') {
            var disconnect = document.createElement('button');
            disconnect.type = 'button'; disconnect.className = 'emby-button'; disconnect.textContent = 'Disconnect';
            scope.on(disconnect, 'click', function () {
                disconnect.disabled = true;
                Scryer.disconnect().then(scope.guard(function () { refreshVisiblePage(); }), scope.guard(function (error) {
                    disconnect.disabled = false;
                    renderConnectionState(container, 'offline', scope, page, error.message);
                }));
            });
            actions.appendChild(disconnect);
        }
        return container.querySelector('.scryerFeatureBody');
    }
    Scryer.renderConnectionState = renderConnectionState;
    function codeToConnectionState(code) {
        if (code === 'not_configured') return 'unconfigured';
        if (code === 'not_connected') return 'connect';
        if (code === 'authorization_expired') return 'expired';
        if (code === 'scryer_incompatible') return 'incompatible';
        return 'offline';
    }
    Scryer.withConnectionGate = function (container, scope, page, renderFeature) {
        container.innerHTML = Scryer.LOADING_HTML;
        getConnectionStatus().then(scope.guard(function (status) {
            if (!status || !status.configured) { renderConnectionState(container, codeToConnectionState(status && status.failure && status.failure.code), scope, page); return; }
            if (!status.connected) { renderConnectionState(container, codeToConnectionState(status.failure && status.failure.code), scope, page); return; }
            var finish = function (limited) {
                var body = renderConnectionState(container, limited ? 'limited' : 'connected', scope, page);
                if (limited) {
                    body.innerHTML = '<p role="status">You do not have permission to use this page.</p>';
                    return;
                }
                scope.own(renderFeature(body, scope));
            };
            getCapabilities().then(scope.guard(function (capabilities) {
                var allowed = hasPageCapability(page.id, capabilities.libraries);
                finish(!allowed);
            }), scope.guard(function (error) { renderConnectionState(container, codeToConnectionState(error.code), scope, page, error.message); }));
        }), scope.guard(function (error) { renderConnectionState(container, codeToConnectionState(error.code), scope, page, error.message); }));
    };

    function ensureSection() {
        var sidebar = document.querySelector('.mainDrawer-scrollContainer');
        if (!sidebar) return null;
        var section = sidebar.querySelector('.scryerSection');
        if (!section) {
            section = document.createElement('div'); section.className = 'scryerSection'; section.innerHTML = '<h3 class="sidebarHeader">Scryer</h3>';
            var mediaSection = sidebar.querySelector('.libraryMenuOptions');
            if (mediaSection) sidebar.insertBefore(section, mediaSection); else sidebar.appendChild(section);
        }
        return section;
    }
    function injectNav() {
        if (!state.featureConfigReady) return;
        var section = ensureSection();
        if (!section) return;
        PAGES.forEach(function (page) {
            if (section.querySelector('.scryer-nav-' + page.id)) return;
            var link = document.createElement('a');
            link.setAttribute('is', 'emby-linkbutton'); link.className = 'navMenuOption lnkMediaFolder emby-button scryer-nav-' + page.id; link.href = page.route;
            link.innerHTML = '<span class="material-icons navMenuOptionIcon" aria-hidden="true">' + page.icon + '</span><span class="sectionName navMenuOptionText">' + escapeHtml(page.title) + '</span>';
            link.addEventListener('click', function (event) { event.preventDefault(); showPage(page.id); });
            section.appendChild(link);
        });
    }
    function getContainer(page) {
        var existing = document.getElementById('scryer-page-' + page.id);
        if (existing) return existing;
        var root = document.createElement('div');
        root.id = 'scryer-page-' + page.id; root.className = 'page type-interior mainAnimatedPage hide scryer-runtime-owned';
        root.setAttribute('data-title', page.title); root.setAttribute('data-backbutton', 'true'); root.setAttribute('data-url', page.route); root.setAttribute('data-type', 'custom');
        root.innerHTML = '<div data-role="content"><div class="content-primary" id="scryer-content-' + page.id + '"></div></div>';
        (document.querySelector('.mainAnimatedPages') || document.body).appendChild(root);
        return root;
    }
    function mountPage(page) {
        var content = document.getElementById('scryer-content-' + page.id);
        if (content) Scryer.lifecycle.mount(page.id, content, { page: page, contextGeneration: jellyfinContextGeneration });
    }
    function showPage(id, force) {
        var page = pageById(id);
        if (!page || (!force && state.visibleId === id)) return;
        if (window.location.hash !== page.route) history.pushState({ scryerPage: id }, page.title, page.route);
        document.querySelectorAll('.mainAnimatedPage:not(.hide)').forEach(function (active) {
            if (active.id === 'scryer-page-' + id) return;
            if (!state.previousPage) state.previousPage = active;
            active.classList.add('hide');
        });
        if (state.visibleId && state.visibleId !== id) Scryer.lifecycle.unmount(state.visibleId);
        if (force && state.visibleId === id) Scryer.lifecycle.unmount(id);
        var container = getContainer(page);
        container.classList.remove('hide'); state.visibleId = id;
        var title = document.querySelector('.pageTitle');
        if (title) title.textContent = page.title;
        mountPage(page);
    }
    function hidePage() {
        if (!state.visibleId) return;
        var element = document.getElementById('scryer-page-' + state.visibleId);
        Scryer.lifecycle.unmount(state.visibleId);
        if (element) element.classList.add('hide');
        state.visibleId = null;
        if (state.previousPage && !document.querySelector('.mainAnimatedPage:not(.hide)')) state.previousPage.classList.remove('hide');
        state.previousPage = null;
    }
    function refreshVisiblePage() { if (state.visibleId) showPage(state.visibleId, true); }
    function handleNavigation() { var page = pageByRoute(window.location.hash); if (page) showPage(page.id); else if (state.visibleId) hidePage(); }

    function loadFeatureConfiguration() {
        return getCurrentJellyfinContext().then(function (context) {
            if (!context.user) { PAGES = []; Scryer.PAGES = PAGES; state.featureConfigReady = true; state.featureConfigKey = context.key; return; }
            return getWebConfiguration().then(function (data) {
                if (activeJellyfinContextKey !== context.key) return;
                var features = data.features || {};
                PAGES = PAGE_DEFINITIONS.filter(function (page) { return features[page.feature] === true; });
                Scryer.PAGES = PAGES; state.featureConfigReady = true; state.featureConfigKey = context.key;
            }, function () {
                if (activeJellyfinContextKey !== context.key) return;
                PAGES = []; Scryer.PAGES = PAGES; state.featureConfigReady = true; state.featureConfigKey = context.key;
            });
        });
    }
    function isRuntimeSurface(node) {
        var element = node && node.nodeType === 1 ? node : node && node.parentElement;
        return !!(element && element.closest && element.closest('.mainDrawer-scrollContainer, .mainAnimatedPages'));
    }
    function scheduleReconcile() {
        if (state.reconcileTimer || state.disposed) return;
        state.reconcileTimer = window.setTimeout(function () {
            state.reconcileTimer = null;
            getCurrentJellyfinContext().then(function (context) {
                if (state.featureConfigKey !== context.key) return loadFeatureConfiguration().then(function () { injectNav(); handleNavigation(); });
                injectNav(); handleNavigation();
            }).catch(function () {});
        }, 80);
    }
    function onNavigation() { handleNavigation(); }
    function installGlobalRuntime() {
        window.addEventListener('hashchange', onNavigation, true);
        window.addEventListener('popstate', onNavigation, true);
        state.navObserver = new MutationObserver(function (records) {
            for (var index = 0; index < records.length && index < 64; index++) if (isRuntimeSurface(records[index].target)) { scheduleReconcile(); return; }
        });
        state.navObserver.observe(document.body, { childList: true, subtree: true });
    }
    Scryer.disposeRuntime = function () {
        if (state.disposed) return;
        state.disposed = true;
        if (state.reconcileTimer) window.clearTimeout(state.reconcileTimer);
        if (state.apiReadyTimer) window.clearInterval(state.apiReadyTimer);
        if (state.navObserver) state.navObserver.disconnect();
        state.navObserver = null;
        window.removeEventListener('hashchange', onNavigation, true);
        window.removeEventListener('popstate', onNavigation, true);
        Scryer.lifecycle.disposeAll();
        document.querySelectorAll('.scryerSection, [id^="scryer-page-"], .scryer-runtime-owned').forEach(function (element) { element.remove(); });
        state.started = false; state.visibleId = null;
    };
    Scryer.startRuntime = function () {
        if (state.started && !state.disposed) return Promise.resolve();
        state.disposed = false; state.started = true;
        installGlobalRuntime();
        return apiPost('Scryer/Auth/Finalize').catch(function () { return null; }).then(function () { return loadFeatureConfiguration(); }).then(function () {
            injectNav(); handleNavigation();
        });
    };

    runtime.registerModule('core', VERSION);
})();
