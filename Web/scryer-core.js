/* RFC 153 browser runtime: user-scoped API helpers, lifecycle, navigation, and connection UI. */
(function () {
    'use strict';

    var VERSION = '153.12';
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
        { id: 'discovery', feature: 'discovery', title: Strings.pages.discovery || 'Discover', iconPath: 'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm3.5 6.5-2.1 4.9-4.9 2.1 2.1-4.9 4.9-2.1z', route: '#/scryer-discovery' },
        { id: 'calendar', feature: 'calendar', title: Strings.pages.calendar || 'Calendar', iconPath: 'M19 4h-1V2h-2v2H8V2H6v2H5c-1.11 0-1.99.9-1.99 2L3 20c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 16H5V9h14v11z', route: '#/scryer-calendar' },
        { id: 'requests', feature: 'requests', title: Strings.pages.requests || 'Requests', iconPath: 'M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-9 14-4-4 1.41-1.41L10 14.17l6.59-6.59L18 9l-8 8z', route: '#/scryer-requests' },
        { id: 'download', feature: 'downloads', title: Strings.pages.downloads || 'Downloads', iconPath: 'M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z', route: '#/scryer-download' }
    ];
    var PAGES = PAGE_DEFINITIONS.slice();
    var OAUTH_WINDOW_NAME = 'scryer-oauth';
    var OAUTH_POPUP_MARKER = 'scryer-oauth-popup';
    var OAUTH_POLL_LIMIT = 180;
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
        started: false, disposed: false, navObserver: null, reconcileTimer: null, apiReadyTimer: null,
        finalizeFailure: null, oauthWindow: null, oauthPollTimer: null
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
        if (document.body && document.body.classList) document.body.classList.remove('scryerPageActive');
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
    function knownFailureCode(code) {
        return typeof code === 'string' && Object.prototype.hasOwnProperty.call(FAILURE_STRING_KEYS, code) ? code : 'internal_error';
    }
    function safeHttpStatus(status) {
        return typeof status === 'number' && Number.isFinite(status) && Math.floor(status) === status && status >= 100 && status <= 599 ? status : 0;
    }
    function failureMessage(code) {
        var key = FAILURE_STRING_KEYS[knownFailureCode(code)];
        return (key && Strings.states && Strings.states[key]) || (Strings.states && Strings.states.internalError) || 'The Scryer request could not be completed.';
    }
    Scryer.failureMessage = failureMessage;

    function normalizeApiPayload(value) {
        if (Array.isArray(value)) return value.map(normalizeApiPayload);
        if (!value || typeof value !== 'object') return value;
        var normalized = {};
        Object.keys(value).forEach(function (sourceKey) {
            if (sourceKey === '__proto__' || sourceKey === 'constructor' || sourceKey === 'prototype') return;
            var targetKey = /^[A-Z]/.test(sourceKey) ? sourceKey.charAt(0).toLowerCase() + sourceKey.slice(1) : sourceKey;
            if (Object.prototype.hasOwnProperty.call(normalized, targetKey)) throw new Error('Jellyfin returned ambiguous response fields.');
            normalized[targetKey] = normalizeApiPayload(value[sourceKey]);
        });
        return normalized;
    }
    Scryer._testing.normalizeApiPayload = normalizeApiPayload;

    function parseApiResponse(response) {
        if (!response || typeof response.text !== 'function') return Promise.resolve(normalizeApiPayload(response));
        return response.text().then(function (text) {
            var payload = null;
            if (text) try { payload = JSON.parse(text); } catch (error) {}
            payload = normalizeApiPayload(payload);
            if (!response.ok) {
                var failureCode = knownFailureCode(payload && Object.prototype.hasOwnProperty.call(payload, 'code') ? payload.code : null);
                var failure = new Error(failureMessage(failureCode));
                failure.code = failureCode;
                failure.status = safeHttpStatus(response.status);
                throw failure;
            }
            return payload;
        });
    }

    function pluginApiUrl(client, path) {
        var clientUrl = new URL(client.getUrl(path), window.location.href);
        return new URL(clientUrl.pathname + clientUrl.search, window.location.origin + '/').href;
    }
    Scryer._testing.pluginApiUrl = pluginApiUrl;
    function apiCallForClient(client, method, path, body) {
        return client.ajax({ type: method, url: pluginApiUrl(client, path), data: body ? JSON.stringify(body) : undefined, contentType: body ? 'application/json' : undefined }).then(parseApiResponse, function (error) {
            if (error && typeof error.text === 'function') return parseApiResponse(error);
            throw error;
        });
    }
    function apiCall(method, path, body) {
        return getCurrentJellyfinContext().then(function (context) {
            if (!context.user) throw new Error('No authenticated Jellyfin user is active.');
            return apiCallForClient(context.client, method, path, body).then(function (data) { return requireCurrentContext(context, data); });
        }).catch(function (error) {
            var diagnosticCode = error && typeof error.code === 'string' && Object.prototype.hasOwnProperty.call(FAILURE_STRING_KEYS, error.code) ? error.code : 'transport_error';
            if (error && (error.code === 'context_changed' || error.message === 'The active Jellyfin context changed.')) diagnosticCode = 'context_changed';
            if (error && error.message === 'Jellyfin API client unavailable') diagnosticCode = 'api_client_unavailable';
            console.error('[Scryer] API request failed:', diagnosticCode, safeHttpStatus(error && error.status));
            throw error;
        });
    }
    function apiGet(path) { return apiCall('GET', path); }
    function apiPost(path, body) { return apiCall('POST', path, body); }
    function apiPut(path, body) { return apiCall('PUT', path, body); }
    Scryer.apiGet = apiGet;
    Scryer.apiPost = apiPost;
    Scryer.apiPut = apiPut;

    function jellyfinItems(response) {
        return response && (response.Items || response.items) || [];
    }
    function jellyfinProviderIds(item) {
        var raw = item && (item.ProviderIds || item.providerIds) || {};
        var ids = {};
        Object.keys(raw).forEach(function (key) {
            var normalized = key.toLowerCase();
            if ((normalized === 'tmdb' || normalized === 'tvdb' || normalized === 'imdb') && raw[key] != null && String(raw[key]).trim()) {
                ids[normalized] = String(raw[key]).trim();
            }
        });
        return ids;
    }
    function getRecentWatchSeeds(limit) {
        var requestedLimit = Math.max(1, Math.min(Number(limit) || 5, 5));
        return getCurrentJellyfinContext().then(function (context) {
            if (!context.user || !context.userId || !context.client.getItems) return [];
            return Promise.resolve(context.client.getItems(context.userId, {
                SortBy: 'DatePlayed', SortOrder: 'Descending', IncludeItemTypes: 'Movie,Episode',
                Recursive: true, Fields: 'ProviderIds', Filters: 'IsPlayed',
                Limit: Math.max(25, requestedLimit * 5), EnableTotalRecordCount: false
            })).then(function (response) {
                requireCurrentContext(context, response);
                var candidates = [];
                var seen = {};
                jellyfinItems(response).some(function (item) {
                    var type = String(item.Type || item.type || '').toLowerCase();
                    var entityId = type === 'episode' ? item.SeriesId || item.seriesId : item.Id || item.id;
                    if ((type !== 'movie' && type !== 'episode') || !entityId || seen[entityId]) return false;
                    seen[entityId] = true;
                    candidates.push({
                        entityId: String(entityId),
                        title: String(type === 'episode' ? item.SeriesName || item.seriesName || item.Name || item.name || 'Series' : item.Name || item.name || 'Movie'),
                        kind: type === 'episode' ? 'SERIES' : 'MOVIE',
                        providerIds: type === 'movie' ? jellyfinProviderIds(item) : {}
                    });
                    return candidates.length >= requestedLimit * 2;
                });
                var seriesIds = candidates.filter(function (candidate) { return candidate.kind === 'SERIES'; }).map(function (candidate) { return candidate.entityId; });
                if (!seriesIds.length) return candidates;
                return Promise.resolve(context.client.getItems(context.userId, {
                    Ids: seriesIds.join(','), Fields: 'ProviderIds', Limit: seriesIds.length, EnableTotalRecordCount: false
                })).then(function (seriesResponse) {
                    requireCurrentContext(context, seriesResponse);
                    var seriesById = {};
                    jellyfinItems(seriesResponse).forEach(function (item) { seriesById[String(item.Id || item.id)] = item; });
                    candidates.forEach(function (candidate) {
                        if (candidate.kind !== 'SERIES' || !seriesById[candidate.entityId]) return;
                        candidate.providerIds = jellyfinProviderIds(seriesById[candidate.entityId]);
                        candidate.title = String(seriesById[candidate.entityId].Name || seriesById[candidate.entityId].name || candidate.title);
                    });
                    return candidates;
                });
            }).then(function (candidates) {
                return requireCurrentContext(context, candidates.filter(function (candidate) {
                    return Object.keys(candidate.providerIds).length > 0;
                }).slice(0, requestedLimit));
            });
        });
    }
    Scryer.getRecentWatchSeeds = getRecentWatchSeeds;

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
            return base ? new URL(url, base).href : url;
        }).catch(function () { return url; });
    }
    Scryer.resolveImageUrl = resolveImageUrl;
    function getConnectionStatus() { return apiGet('Scryer/Auth/Status'); }
    Scryer.getConnectionStatus = getConnectionStatus;
    function openOAuthWindow() {
        var width = 800;
        var height = 700;
        var left = Math.max(0, Math.round((window.screenX || window.screenLeft || 0) + ((window.outerWidth || width) - width) / 2));
        var top = Math.max(0, Math.round((window.screenY || window.screenTop || 0) + ((window.outerHeight || height) - height) / 2));
        return window.open('', OAUTH_WINDOW_NAME, 'popup=yes,width=' + width + ',height=' + height + ',left=' + left + ',top=' + top + ',resizable=yes,scrollbars=yes');
    }
    function clearOAuthPolling() {
        if (state.oauthPollTimer) window.clearTimeout(state.oauthPollTimer);
        state.oauthPollTimer = null;
    }
    function closeOAuthWindow(popup) {
        try { if (popup && !popup.closed) popup.close(); } catch (error) {}
    }
    function completeOAuthConnection(popup) {
        clearOAuthPolling();
        closeOAuthWindow(popup);
        if (state.oauthWindow === popup) state.oauthWindow = null;
        state.finalizeFailure = null;
        refreshVisiblePage();
    }
    function pollOAuthCompletion(popup, remaining) {
        if (state.disposed || state.oauthWindow !== popup) return;
        apiPost('Scryer/Auth/Finalize').catch(function () { return null; }).then(function () {
            return getConnectionStatus();
        }).then(function (status) {
            if (status && status.connected) {
                completeOAuthConnection(popup);
                return;
            }
            if (remaining <= 1) {
                clearOAuthPolling();
                closeOAuthWindow(popup);
                if (state.oauthWindow === popup) state.oauthWindow = null;
                var failure = new Error('Scryer authorization did not complete. Try Connect again.');
                failure.code = 'authorization_expired';
                state.finalizeFailure = failure;
                refreshVisiblePage();
                return;
            }
            state.oauthPollTimer = window.setTimeout(function () { pollOAuthCompletion(popup, remaining - 1); }, 1000);
        }, function () {
            state.oauthPollTimer = window.setTimeout(function () { pollOAuthCompletion(popup, remaining - 1); }, 1000);
        });
    }
    function startConnection(returnPage) {
        var page = pageByRoute(returnPage || window.location.hash);
        var target = page ? page.route : '#/scryer-discovery';
        var popup = openOAuthWindow();
        if (!popup) return Promise.reject(new Error('Allow pop-ups for Jellyfin, then try Connect again.'));
        state.oauthWindow = popup;
        try { popup.sessionStorage.setItem(OAUTH_POPUP_MARKER, '1'); } catch (error) {}
        popup.focus();
        return apiPost('Scryer/Auth/Start', { returnPage: target }).then(function (data) {
            if (!data || typeof data.authorizationUrl !== 'string' || data.authorizationUrl.length > 4096) throw new Error('Scryer returned an invalid authorization redirect.');
            popup.location.replace(data.authorizationUrl);
            pollOAuthCompletion(popup, OAUTH_POLL_LIMIT);
        }).catch(function (error) {
            clearOAuthPolling();
            closeOAuthWindow(popup);
            if (state.oauthWindow === popup) state.oauthWindow = null;
            throw error;
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
            if (pageId === 'discovery') return library.canView || library.canRequest || library.canManageTitles;
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
            connecting: 'Connecting Scryer…',
            expired: Strings.states.authorizationExpired || 'Your Scryer connection expired. Connect again.', offline: Strings.states.offline || 'Scryer is currently unreachable.',
            incompatible: Strings.states.incompatible || 'This Scryer server is incompatible.'
        };
        var message = escapeHtml(detail || messages[kind] || messages.offline);
        if (kind === 'connected' || kind === 'anonymous' || kind === 'limited') {
            container.innerHTML = '<div class="scryerFeatureBody"></div>';
        } else if (kind === 'connect' || kind === 'expired') {
            container.innerHTML = '<div class="scryerConnectCard" role="status" aria-live="polite">' +
                '<div class="scryerConnectBrands">' +
                    '<img class="scryerConnectLogo scryerConnectScryerLogo" src="/Scryer/Web/scryer-logo.svg" alt="Scryer">' +
                    '<svg class="scryerConnectArrows" viewBox="0 0 70 44" aria-hidden="true"><path d="M5 12h48m0 0-9-8m9 8-9 8M65 32H17m0 0 9-8m-9 8 9 8"/></svg>' +
                    '<img class="scryerConnectLogo scryerConnectJellyfinLogo" src="/Scryer/Web/jellyfin-logo.svg" alt="Jellyfin">' +
                '</div>' +
                '<p class="scryerConnectMessage">' + message + '</p>' +
                '<div class="scryerConnectionActions"></div>' +
            '</div><div class="scryerFeatureBody"></div>';
        } else {
            container.innerHTML = '<div class="scryerBanner scryerBannerWarning" role="status" aria-live="polite"><span>' + message + '</span><span class="scryerConnectionActions"></span></div><div class="scryerFeatureBody"></div>';
        }
        var actions = container.querySelector('.scryerConnectionActions');
        if (kind === 'connect' || kind === 'expired') {
            var connect = document.createElement('button');
            connect.type = 'button'; connect.className = 'raised button-submit scryerConnectButton'; connect.textContent = 'Connect';
            scope.on(connect, 'click', function () {
                state.finalizeFailure = null;
                connect.disabled = true;
                renderConnectionState(container, 'connecting', scope, page);
                startConnection(page.route).catch(scope.guard(function (error) { renderConnectionState(container, 'offline', scope, page, error.message); }));
            });
            actions.appendChild(connect);
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
    function connectionStateForStatus(status) {
        var failureCode = status && status.failure && status.failure.code;
        if (!status || !status.configured) return codeToConnectionState(failureCode || 'not_configured');
        if (!status.connected) return codeToConnectionState(failureCode || 'not_connected');
        return null;
    }
    Scryer._testing.connectionStateForStatus = connectionStateForStatus;
    Scryer.withConnectionGate = function (container, scope, page, renderFeature) {
        container.innerHTML = Scryer.LOADING_HTML;
        getConnectionStatus().then(scope.guard(function (status) {
            var connectionState = connectionStateForStatus(status);
            if (connectionState) {
                var finalizeFailure = state.finalizeFailure;
                renderConnectionState(container, finalizeFailure ? codeToConnectionState(finalizeFailure.code) : connectionState, scope, page, finalizeFailure && finalizeFailure.message);
                return;
            }
            state.finalizeFailure = null;
            if (status.accountLinked === false) {
                var anonymousBody = renderConnectionState(container, 'anonymous', scope, page);
                scope.own(renderFeature(anonymousBody, scope));
                return;
            }
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
            link.innerHTML = '<svg class="navMenuOptionIcon scryerNavIcon" viewBox="0 0 24 24" width="24" height="24" focusable="false" aria-hidden="true"><path fill="currentColor" d="' + page.iconPath + '"></path></svg><span class="sectionName navMenuOptionText">' + escapeHtml(page.title) + '</span>';
            link.addEventListener('click', function (event) {
                event.preventDefault();
                event.stopImmediatePropagation();
                showPage(page.id);
            }, true);
            section.appendChild(link);
        });
    }
    function getContainer(page) {
        var existing = document.getElementById('scryer-page-' + page.id);
        if (existing) return existing;
        var root = document.createElement('div');
        root.id = 'scryer-page-' + page.id; root.className = 'page type-interior libraryPage mainAnimatedPage hide scryer-runtime-owned';
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
        if (document.body && document.body.classList) document.body.classList.add('scryerPageActive');
        mountPage(page);
    }
    function hidePage() {
        if (document.body && document.body.classList) document.body.classList.remove('scryerPageActive');
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
    function onOAuthMessage(event) {
        if (event.origin !== window.location.origin || event.source !== state.oauthWindow || !event.data || event.data.type !== 'scryer-oauth-complete') return;
        clearOAuthPolling();
        state.oauthWindow = null;
        if (event.data.success) {
            state.finalizeFailure = null;
        } else {
            var failure = new Error(typeof event.data.message === 'string' ? event.data.message : failureMessage(event.data.code));
            failure.code = knownFailureCode(event.data.code);
            state.finalizeFailure = failure;
        }
        refreshVisiblePage();
    }
    function isOAuthPopup() {
        if (window.name === OAUTH_WINDOW_NAME) return true;
        try { return window.sessionStorage.getItem(OAUTH_POPUP_MARKER) === '1'; } catch (error) { return false; }
    }
    function finishOAuthPopup(status, error) {
        if (!isOAuthPopup()) return false;
        if (!error && !(status && status.connected)) return false;
        var openerAvailable = !!(window.opener && !window.opener.closed);
        if (openerAvailable) {
            window.opener.postMessage({
                type: 'scryer-oauth-complete',
                success: !error,
                code: error ? knownFailureCode(error.code) : null,
                message: error && error.message ? error.message : null
            }, window.location.origin);
        }
        if (error && !openerAvailable) return false;
        try { window.sessionStorage.removeItem(OAUTH_POPUP_MARKER); } catch (storageError) {}
        window.close();
        return true;
    }
    function installGlobalRuntime() {
        window.addEventListener('hashchange', onNavigation, true);
        window.addEventListener('popstate', onNavigation, true);
        window.addEventListener('message', onOAuthMessage, false);
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
        clearOAuthPolling();
        if (state.navObserver) state.navObserver.disconnect();
        state.navObserver = null;
        window.removeEventListener('hashchange', onNavigation, true);
        window.removeEventListener('popstate', onNavigation, true);
        window.removeEventListener('message', onOAuthMessage, false);
        Scryer.lifecycle.disposeAll();
        document.querySelectorAll('.scryerSection, [id^="scryer-page-"], .scryer-runtime-owned').forEach(function (element) { element.remove(); });
        if (document.body && document.body.classList) document.body.classList.remove('scryerPageActive');
        state.started = false; state.visibleId = null;
    };
    Scryer.startRuntime = function () {
        if (state.started && !state.disposed) return Promise.resolve();
        state.disposed = false; state.started = true;
        installGlobalRuntime();
        var popupFinished = false;
        return apiPost('Scryer/Auth/Finalize').then(function (status) {
            state.finalizeFailure = null;
            popupFinished = finishOAuthPopup(status, null);
        }, function (error) {
            state.finalizeFailure = error;
            popupFinished = finishOAuthPopup(null, error);
        }).then(function () { return popupFinished ? null : loadFeatureConfiguration(); }).then(function () {
            if (popupFinished) return;
            injectNav(); handleNavigation();
        });
    };

    runtime.registerModule('core', VERSION);
})();
