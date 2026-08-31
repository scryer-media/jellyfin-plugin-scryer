(function () {
    'use strict';

    var Scryer = window.Scryer;
    var apiGet = Scryer.apiGet, apiPost = Scryer.apiPost, escapeHtml = Scryer.escapeHtml;
    var whenApiClientReady = Scryer.whenApiClientReady, getLibraries = Scryer.getLibraries;
    var LOADING_HTML = Scryer.LOADING_HTML;

    var FACETS = ['MOVIE', 'SERIES', 'ANIME'];
    var STATUSES = [
        { value: 'PENDING', label: 'Pending' },
        { value: 'APPROVED', label: 'Approved' },
        { value: 'REJECTED', label: 'Dismissed' }
    ];

    function renderRequests(container) {
        container.innerHTML =
            '<h1>Requests</h1>' +
            '<div class="scryerRequestTabs">' +
                '<div class="scryerTabGroup scryerFacetTabs"></div>' +
                '<div class="scryerTabGroup scryerStatusTabs"></div>' +
                '<select is="emby-select" class="scryerLibraryFilter hide"></select>' +
            '</div>' +
            '<div class="scryerRequestsList">' + LOADING_HTML + '</div>';

        var facetTabsEl = container.querySelector('.scryerFacetTabs');
        var statusTabsEl = container.querySelector('.scryerStatusTabs');
        var libraryFilter = container.querySelector('.scryerLibraryFilter');
        var list = container.querySelector('.scryerRequestsList');

        var isAdmin = false;
        var libraries = [];
        var allRequests = [];
        var state = { facet: 'ALL', status: 'PENDING', libraryId: 'ALL' };

        function count(predicate) {
            return allRequests.filter(predicate).length;
        }

        function renderTabs() {
            facetTabsEl.innerHTML =
                tabHtml('ALL', 'All', state.facet === 'ALL', count(function () { return true; })) +
                FACETS.map(function (f) {
                    return tabHtml(f, f.charAt(0) + f.slice(1).toLowerCase(), state.facet === f,
                        count(function (r) { return r.facet === f; }));
                }).join('');

            statusTabsEl.innerHTML = STATUSES.map(function (s) {
                return tabHtml(s.value, s.label, state.status === s.value,
                    count(function (r) { return r.status === s.value; }));
            }).join('');
        }

        function tabHtml(value, label, active, n) {
            return '<button type="button" class="scryerTab' + (active ? ' scryerTabActive' : '') +
                '" data-value="' + value + '">' + escapeHtml(label) +
                ' <span class="scryerTabCount">' + n + '</span></button>';
        }

        function renderRows() {
            var filtered = allRequests.filter(function (r) {
                if (state.facet !== 'ALL' && r.facet !== state.facet) return false;
                if (state.status !== 'ALL' && r.status !== state.status) return false;
                if (state.libraryId !== 'ALL' && r.libraryId !== state.libraryId) return false;
                return true;
            });

            list.innerHTML = '';
            if (!filtered.length) {
                list.innerHTML = '<p>No requests for this filter.</p>';
                return;
            }

            filtered.forEach(function (r) {
                var actions = isAdmin
                    ? '<button is="emby-button" class="raised" data-action="approve" data-id="' + r.id + '" data-library="' + r.libraryId + '">Approve</button> ' +
                      '<button is="emby-button" class="raised" data-action="dismiss" data-id="' + r.id + '">Dismiss</button>'
                    : '';
                var row = document.createElement('div');
                row.className = 'scryerRow';
                row.innerHTML =
                    '<span>' + escapeHtml(r.title) + '</span>' +
                    '<span class="scryerStatus-' + r.status + '">' + r.status + '</span>' +
                    '<span>' + actions + '</span>';
                list.appendChild(row);
            });
        }

        function load() {
            list.innerHTML = LOADING_HTML;
            var endpoint = isAdmin ? 'Scryer/Requests' : 'Scryer/Requests/Mine';
            apiGet(endpoint).then(function (data) {
                allRequests = data.mediaRequests || data.myMediaRequests || [];
                renderTabs();
                renderRows();
            });
        }

        facetTabsEl.addEventListener('click', function (e) {
            var btn = e.target.closest('.scryerTab');
            if (!btn) return;
            state.facet = btn.dataset.value;
            renderTabs();
            renderRows();
        });

        statusTabsEl.addEventListener('click', function (e) {
            var btn = e.target.closest('.scryerTab');
            if (!btn) return;
            state.status = btn.dataset.value;
            renderTabs();
            renderRows();
        });

        libraryFilter.addEventListener('change', function () {
            state.libraryId = libraryFilter.value;
            renderRows();
        });

        whenApiClientReady()
            .then(function (client) { return client && client.getCurrentUser ? client.getCurrentUser() : null; })
            .then(function (u) { isAdmin = !!(u && u.Policy && u.Policy.IsAdministrator); })
            .then(function () {
                if (!isAdmin) return null;
                return getLibraries().then(function (l) {
                    libraries = l;
                    libraryFilter.classList.remove('hide');
                    libraryFilter.innerHTML = '<option value="ALL">All Libraries</option>' +
                        libraries.map(function (lib) {
                            return '<option value="' + lib.id + '">' + escapeHtml(lib.name) + '</option>';
                        }).join('');
                });
            })
            .then(load);

        list.addEventListener('click', function (e) {
            var button = e.target.closest('button[data-action]');
            if (!button) return;

            if (button.dataset.action === 'approve') {
                var lib = libraries.filter(function (l) { return l.id === button.dataset.library; })[0];
                if (!lib || !lib.qualityProfileId) {
                    window.Dashboard?.alert('No quality profile found for this library');
                    return;
                }
                apiPost('Scryer/Requests/' + button.dataset.id + '/approve?qualityProfileId=' + encodeURIComponent(lib.qualityProfileId))
                    .then(load)
                    .catch(function (err) { window.Dashboard?.alert('Action failed: ' + err.message); });
                return;
            }

            apiPost('Scryer/Requests/' + button.dataset.id + '/dismiss')
                .then(load)
                .catch(function (err) { window.Dashboard?.alert('Action failed: ' + err.message); });
        });
    }

    Scryer.pages.requests = renderRequests;
})();
