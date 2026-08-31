/*
 * Scryer core: shared window.Scryer namespace, API helpers, sidebar injection,
 * fake client-side router. Each page (Discover/Calendar/Requests/Downloads)
 * lives in its own scryer-*.js and registers into Scryer.pages[id] = fn(container).
 * All files load as separate <script defer> tags in order and share state via
 * window.Scryer -- no bundler in this build to merge them into one file.
 */
(function () {
    'use strict';

    var Scryer = window.Scryer = window.Scryer || {};
    Scryer.pages = Scryer.pages || {};

    var PAGES = [
        { id: 'discovery', title: 'Discover', icon: 'explore', route: '#/scryer-discovery' },
        { id: 'calendar', title: 'Calendar', icon: 'event', route: '#/scryer-calendar' },
        { id: 'requests', title: 'Requests', icon: 'playlist_add_check', route: '#/scryer-requests' },
        { id: 'download', title: 'Downloads', icon: 'download', route: '#/scryer-download' }
    ];
    Scryer.PAGES = PAGES;

    function pageById(id) {
        for (var i = 0; i < PAGES.length; i++) {
            if (PAGES[i].id === id) return PAGES[i];
        }
        return null;
    }

    function pageByRoute(hash) {
        for (var i = 0; i < PAGES.length; i++) {
            if (PAGES[i].route === hash) return PAGES[i];
        }
        return null;
    }

    // ---- API helpers ----------------------------------------------------

    function isApiClientConnected(client) {
        return !!(client && client.getUrl && client.serverAddress && client.serverAddress());
    }

    var apiClientReadyPromise = null;
    function whenApiClientReady() {
        if (isApiClientConnected(window.ApiClient)) {
            return Promise.resolve(window.ApiClient);
        }

        if (!apiClientReadyPromise) {
            apiClientReadyPromise = new Promise(function (resolve) {
                var attempts = 0;
                var check = setInterval(function () {
                    attempts++;
                    if (isApiClientConnected(window.ApiClient)) {
                        clearInterval(check);
                        resolve(window.ApiClient);
                    } else if (attempts > 200) {
                        clearInterval(check);
                        resolve(window.ApiClient || null);
                    }
                }, 100);
            });
        }
        return apiClientReadyPromise;
    }
    Scryer.whenApiClientReady = whenApiClientReady;

    var imageBaseUrlPromise = null;
    function resolveImageUrl(url) {
        if (!url || !url.startsWith('/')) {
            return Promise.resolve(url);
        }

        if (!imageBaseUrlPromise) {
            imageBaseUrlPromise = fetch('/Scryer/Web/config')
                .then(function (r) { return r.json(); })
                .then(function (data) { return data.imageBaseUrl || ''; })
                .catch(function () { return ''; });
        }

        return imageBaseUrlPromise.then(function (base) {
            return base ? base.replace(/\/$/, '') + url : url;
        });
    }
    Scryer.resolveImageUrl = resolveImageUrl;

    function apiGet(path) {
        return whenApiClientReady().then(function (client) {
            return client.getJSON(client.getUrl(path));
        });
    }
    Scryer.apiGet = apiGet;

    function apiCall(method, path, body) {
        return whenApiClientReady().then(function (client) {
            return client.ajax({
                type: method,
                url: client.getUrl(path),
                data: body ? JSON.stringify(body) : undefined,
                contentType: body ? 'application/json' : undefined
            });
        }).then(function (r) {
            if (r && typeof r.json === 'function') {
                if (!r.ok) throw new Error('request failed: ' + r.status);
                return r.text().then(function (t) {
                    if (!t) return null;
                    try { return JSON.parse(t); } catch (e) { return null; }
                });
            }
            return r;
        });
    }

    function apiPost(path, body) { return apiCall('POST', path, body); }
    Scryer.apiPost = apiPost;

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    Scryer.escapeHtml = escapeHtml;

    Scryer.LOADING_HTML = '<div class="scryerLoading"><div class="scryerSpinner"></div></div>';

    var librariesCache = null;
    function getLibraries() {
        if (librariesCache) return Promise.resolve(librariesCache);
        return apiGet('Scryer/Libraries').then(function (data) {
            librariesCache = data.libraries || [];
            return librariesCache;
        });
    }
    Scryer.getLibraries = getLibraries;

    var isAdminPromise = null;
    function isAdminUser() {
        if (!isAdminPromise) {
            isAdminPromise = whenApiClientReady()
                .then(function (client) { return client && client.getCurrentUser ? client.getCurrentUser() : null; })
                .then(function (u) { return !!(u && u.Policy && u.Policy.IsAdministrator); })
                .catch(function () { return false; });
        }
        return isAdminPromise;
    }
    Scryer.isAdminUser = isAdminUser;

    var qualityProfilesCache = null;
    function getQualityProfiles() {
        if (qualityProfilesCache) return Promise.resolve(qualityProfilesCache);
        return apiGet('Scryer/Catalog/QualityProfiles').then(function (data) {
            qualityProfilesCache = (data.qualityProfileSettings && data.qualityProfileSettings.profiles) || [];
            return qualityProfilesCache;
        });
    }
    Scryer.getQualityProfiles = getQualityProfiles;

    function facetOf(item) {
        var raw = (item.targetKind || item.facet || 'MOVIE').toUpperCase();
        return raw === 'SERIES' || raw === 'ANIME' ? raw : 'MOVIE';
    }
    Scryer.facetOf = facetOf;

    function populateSelect(select, options, valueKey, labelKey, selectedValue) {
        select.innerHTML = options.map(function (o) {
            var value = o[valueKey];
            var selected = value === selectedValue ? ' selected' : '';
            return '<option value="' + escapeHtml(value) + '"' + selected + '>' + escapeHtml(o[labelKey]) + '</option>';
        }).join('');
    }
    Scryer.populateSelect = populateSelect;

    // ---- Sidebar injection ------------------------------------------------

    function ensureSection() {
        var sidebar = document.querySelector('.mainDrawer-scrollContainer');
        if (!sidebar) return null;

        var section = sidebar.querySelector('.scryerSection');
        if (!section) {
            section = document.createElement('div');
            section.className = 'scryerSection';
            section.innerHTML = '<h3 class="sidebarHeader">Scryer</h3>';

            var mediaSection = sidebar.querySelector('.libraryMenuOptions');
            if (mediaSection) {
                sidebar.insertBefore(section, mediaSection);
            } else {
                sidebar.appendChild(section);
            }
        }
        return section;
    }

    function injectNav() {
        var section = ensureSection();
        if (!section) return;

        PAGES.forEach(function (page) {
            var existing = section.querySelector('.scryer-nav-' + page.id);
            if (existing) return;

            var link = document.createElement('a');
            link.setAttribute('is', 'emby-linkbutton');
            link.className = 'navMenuOption lnkMediaFolder emby-button scryer-nav-' + page.id;
            link.href = page.route;
            link.innerHTML =
                '<span class="material-icons navMenuOptionIcon" aria-hidden="true">' + page.icon + '</span>' +
                '<span class="sectionName navMenuOptionText">' + page.title + '</span>';

            link.addEventListener('click', function (e) {
                e.preventDefault();
                showPage(page.id);
            });

            section.appendChild(link);
        });
    }

    var state = { visibleId: null, previousPage: null };

    function getContainer(id) {
        var existing = document.getElementById('scryer-page-' + id);
        if (existing) return existing;

        var page = pageById(id);
        var div = document.createElement('div');
        div.id = 'scryer-page-' + id;
        div.className = 'page type-interior mainAnimatedPage hide';
        div.setAttribute('data-title', page.title);
        div.setAttribute('data-backbutton', 'true');
        div.setAttribute('data-url', page.route);
        div.setAttribute('data-type', 'custom');
        div.innerHTML =
            '<div data-role="content"><div class="content-primary" id="scryer-content-' + id + '"></div></div>';

        var mainContent = document.querySelector('.mainAnimatedPages');
        (mainContent || document.body).appendChild(div);
        return div;
    }

    function renderPage(id, container) {
        if (!container) return;
        container.innerHTML = Scryer.LOADING_HTML;

        var renderer = Scryer.pages[id];
        if (renderer) renderer(container);
    }

    function showPage(id) {
        if (state.visibleId === id) return;
        var page = pageById(id);
        if (!page) return;

        var discoveryModal = document.getElementById('scryer-discovery-modal');
        var discoveryBackdrop = document.getElementById('scryer-discovery-backdrop');
        if (discoveryModal) discoveryModal.classList.add('hide');
        if (discoveryBackdrop) discoveryBackdrop.classList.add('hide');

        if (window.location.hash !== page.route) {
            history.pushState({ scryerPage: id }, page.title, page.route);
        }

        var others = document.querySelectorAll('.mainAnimatedPage:not(.hide)');
        state.previousPage = null;
        others.forEach(function (active) {
            if (active.id === 'scryer-page-' + id) return;
            if (!state.previousPage) state.previousPage = active;
            active.classList.add('hide');
            active.dispatchEvent(new CustomEvent('viewhide', { bubbles: true, detail: { type: 'interior' } }));
        });

        var container = getContainer(id);
        container.classList.remove('hide');
        state.visibleId = id;

        container.dispatchEvent(new CustomEvent('viewshow', {
            bubbles: true,
            detail: { type: 'custom', isRestored: false, options: {} }
        }));

        var titleEl = document.querySelector('.pageTitle');
        if (titleEl) titleEl.textContent = page.title;

        renderPage(id, document.getElementById('scryer-content-' + id));
    }

    function hidePage() {
        if (!state.visibleId) return;

        var el = document.getElementById('scryer-page-' + state.visibleId);
        if (el) {
            el.classList.add('hide');
            el.dispatchEvent(new CustomEvent('viewhide', { bubbles: true, detail: { type: 'custom' } }));
        }
        state.visibleId = null;

        if (state.previousPage && !document.querySelector('.mainAnimatedPage:not(.hide)')) {
            state.previousPage.classList.remove('hide');
            state.previousPage.dispatchEvent(new CustomEvent('viewshow', {
                bubbles: true,
                detail: { type: 'interior', isRestored: true }
            }));
        }
        state.previousPage = null;
    }

    function handleNavigation() {
        var match = pageByRoute(window.location.hash);
        if (match) {
            showPage(match.id);
        } else if (state.visibleId) {
            hidePage();
        }
    }

    function interceptNavigation(e) {
        var hash = (e && e.newURL) ? new URL(e.newURL).hash : window.location.hash;
        if (pageByRoute(hash)) {
            if (e && e.stopImmediatePropagation) e.stopImmediatePropagation();
            handleNavigation();
        }
    }

    function init() {
        injectNav();
        handleNavigation();
    }

    window.addEventListener('hashchange', interceptNavigation, true);
    window.addEventListener('popstate', interceptNavigation, true);

    function suppressForeignPages() {
        if (!state.visibleId) return;
        document.querySelectorAll('.mainAnimatedPage:not(.hide)').forEach(function (el) {
            if (el.id !== 'scryer-page-' + state.visibleId) {
                el.classList.add('hide');
            }
        });

        var page = pageById(state.visibleId);
        var titleEl = document.querySelector('.pageTitle');
        if (page && titleEl && titleEl.textContent !== page.title) {
            titleEl.textContent = page.title;
        }
    }

    var lastKnownHash = window.location.hash;
    var navObserver = new MutationObserver(function () {
        injectNav();
        suppressForeignPages();

        if (window.location.hash !== lastKnownHash) {
            lastKnownHash = window.location.hash;
            handleNavigation();
        }
    });

    function whenAllScriptsReady(callback) {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', callback);
        } else {
            setTimeout(callback, 0);
        }
    }

    whenAllScriptsReady(function () {
        init();
        navObserver.observe(document.body, { childList: true, subtree: true });
    });
})();
