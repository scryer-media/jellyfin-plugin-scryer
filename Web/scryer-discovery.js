(function () {
    'use strict';

    var Scryer = window.Scryer;
    var apiGet = Scryer.apiGet, apiPost = Scryer.apiPost, escapeHtml = Scryer.escapeHtml;
    var resolveImageUrl = Scryer.resolveImageUrl, LOADING_HTML = Scryer.LOADING_HTML;
    var getLibraries = Scryer.getLibraries, isAdminUser = Scryer.isAdminUser;
    var getQualityProfiles = Scryer.getQualityProfiles, facetOf = Scryer.facetOf;
    var populateSelect = Scryer.populateSelect;

    // Appended to document.body, not the page container: Jellyfin's page-transition
    // transform on an ancestor breaks position:fixed there. Backdrop and dialog are
    // siblings, not parent/child: .dialogBackdropOpened sets opacity:.66, which
    // cascades to descendants, so nesting the dialog inside it washed it out too.
    function ensureDiscoveryModal() {
        var existingModal = document.getElementById('scryer-discovery-modal');
        if (existingModal) {
            return { backdrop: document.getElementById('scryer-discovery-backdrop'), modal: existingModal };
        }

        var backdrop = document.createElement('div');
        backdrop.id = 'scryer-discovery-backdrop';
        backdrop.className = 'scryerModalBackdrop dialogBackdrop dialogBackdropOpened hide';

        var modal = document.createElement('div');
        modal.id = 'scryer-discovery-modal';
        modal.className = 'scryerModal dialog hide';
        modal.innerHTML =
            '<button type="button" class="scryerModalClose" aria-label="Close">&times;</button>' +
            '<div class="scryerModalPoster"></div>' +
            '<div class="scryerModalBody">' +
                '<h2 class="scryerModalTitle"></h2>' +
                '<div class="scryerModalYear"></div>' +
                '<div class="scryerModalRatings"></div>' +
                '<p class="scryerModalOverview"></p>' +
                '<div class="scryerModalLinks"></div>' +
                '<div class="scryerModalAdminForm hide">' +
                    '<div class="inputContainer">' +
                        '<label class="inputLabel">Library</label>' +
                        '<select is="emby-select" class="scryerLibrarySelect"></select>' +
                    '</div>' +
                    '<div class="inputContainer">' +
                        '<label class="inputLabel">Quality profile</label>' +
                        '<select is="emby-select" class="scryerQualitySelect"></select>' +
                    '</div>' +
                    '<div class="inputContainer">' +
                        '<label class="inputLabel">Root folder</label>' +
                        '<select is="emby-select" class="scryerRootFolderSelect"></select>' +
                    '</div>' +
                    '<label class="scryerMonitoredRow">' +
                        '<input type="checkbox" is="emby-checkbox" class="scryerMonitoredCheckbox" checked />' +
                        '<span>Monitored</span>' +
                    '</label>' +
                '</div>' +
                '<div class="scryerModalMessage"></div>' +
                '<button is="emby-button" type="button" class="raised scryerModalRequestBtn">Request</button>' +
            '</div>';

        // Theme CSS sets .dialogBackdrop.dialogBackdropOpened's z-index to 999998 via a
        // two-class selector (beats our single-class rule regardless of source order);
        // inline style always wins, so it's set directly instead of via the cascade.
        backdrop.style.zIndex = '2147483000';
        modal.style.zIndex = '2147483001';

        document.body.appendChild(backdrop);
        document.body.appendChild(modal);
        return { backdrop: backdrop, modal: modal };
    }

    function renderDiscovery(container) {
        container.innerHTML =
            '<h1>Discover</h1>' +
            '<div class="inputContainer"><input is="emby-input" type="text" class="scryerSearchBox" placeholder="Search movies and shows" /></div>' +
            '<div class="scryerCategories"></div>';

        var categoriesEl = container.querySelector('.scryerCategories');
        var modalRefs = ensureDiscoveryModal();
        var backdrop = modalRefs.backdrop;
        var modal = modalRefs.modal;
        var items = [];

        function posterPlaceholderHtml() {
            return '<div class="scryerPosterPlaceholder"><span class="material-icons" aria-hidden="true">movie</span></div>';
        }

        function renderPosterInto(el, url) {
            el.innerHTML = posterPlaceholderHtml();
            if (!url) return;

            resolveImageUrl(url).then(function (resolved) {
                var img = new Image();
                img.alt = '';
                img.onload = function () {
                    el.innerHTML = '';
                    el.appendChild(img);
                };
                img.src = resolved;
            });
        }

        function cardHtml(item) {
            return '<div class="scryerCardPoster"></div>' +
                '<div class="scryerCardTitle">' + escapeHtml(item.displayTitle || item.name || 'Untitled') + '</div>';
        }

        function renderCategories(categories) {
            items = [];
            categoriesEl.innerHTML = '';
            categories.forEach(function (cat) {
                if (!cat.items || !cat.items.length) return;

                var section = document.createElement('section');
                section.className = 'scryerCategory';
                section.innerHTML =
                    '<h2 class="scryerCategoryTitle">' + escapeHtml(cat.title) + '</h2>' +
                    '<div class="scryerCarousel">' +
                        '<button type="button" class="scryerCarouselNav scryerCarouselPrev" aria-label="Previous">&lsaquo;</button>' +
                        '<div class="scryerRow"></div>' +
                        '<button type="button" class="scryerCarouselNav scryerCarouselNext" aria-label="Next">&rsaquo;</button>' +
                    '</div>';
                categoriesEl.appendChild(section);

                var row = section.querySelector('.scryerRow');
                cat.items.forEach(function (item) {
                    var index = items.length;
                    items.push(item);

                    var card = document.createElement('div');
                    card.className = 'scryerCard';
                    card.dataset.index = index;
                    card.innerHTML = cardHtml(item);
                    row.appendChild(card);
                    renderPosterInto(card.querySelector('.scryerCardPoster'), item.posterUrl);
                });

                section.querySelector('.scryerCarouselPrev').addEventListener('click', function () {
                    row.scrollBy({ left: -row.clientWidth * 0.9, behavior: 'smooth' });
                });
                section.querySelector('.scryerCarouselNext').addEventListener('click', function () {
                    row.scrollBy({ left: row.clientWidth * 0.9, behavior: 'smooth' });
                });
            });
        }

        function renderCards(newItems) {
            renderCategories([{ title: 'Results', items: newItems }]);
        }

        function loadTrending() {
            categoriesEl.innerHTML = LOADING_HTML;
            apiGet('Scryer/Discovery/Trending').then(function (data) {
                var payload = data.discoveryHomeCards || {};
                var categories = [];
                (payload.publicSections || []).forEach(function (s) { categories.push({ title: s.title, items: s.items }); });
                if (payload.canViewPersonalized) {
                    (payload.personalizedSections || []).forEach(function (s) { categories.push({ title: s.title, items: s.items }); });
                }
                renderCategories(categories);
            }).catch(function (err) { categoriesEl.innerHTML = '<p>' + escapeHtml(err.message) + '</p>'; });
        }

        // Trending-grid items (DiscoveryHomeCardPayload) carry no overview/externalIds;
        // only search results and the on-demand item-detail query do.
        function externalIdsFor(item) {
            if (Array.isArray(item.externalIds) && item.externalIds.length) {
                return item.externalIds.map(function (e) { return { Source: e.source, Value: String(e.id) }; });
            }
            var ids = [];
            if (item.tmdbId) ids.push({ Source: 'tmdb', Value: String(item.tmdbId) });
            if (item.tvdbId) ids.push({ Source: 'tvdb', Value: String(item.tvdbId) });
            return ids;
        }

        function externalLinkUrl(source, id, facet) {
            var s = (source || '').toLowerCase();
            if (!id) return null;
            if (s === 'imdb') return 'https://www.imdb.com/title/' + id + '/';
            if (s === 'tmdb') return 'https://www.themoviedb.org/' + (facet === 'MOVIE' ? 'movie' : 'tv') + '/' + id;
            if (s === 'tvdb') return 'https://www.thetvdb.com/dereferrer/' + (facet === 'MOVIE' ? 'movie' : 'series') + '/' + id;
            return null;
        }

        function renderLinks(item) {
            var facet = facetOf(item);
            var pairs = [];
            if (Array.isArray(item.externalIds)) {
                item.externalIds.forEach(function (e) { pairs.push([e.source, e.id]); });
            } else {
                if (item.imdbId) pairs.push(['imdb', item.imdbId]);
                if (item.tmdbId) pairs.push(['tmdb', item.tmdbId]);
                if (item.tvdbId) pairs.push(['tvdb', item.tvdbId]);
            }

            var html = pairs.map(function (p) {
                var url = externalLinkUrl(p[0], p[1], facet);
                if (!url) return '';
                return '<a href="' + escapeHtml(url) + '" target="_blank" rel="noopener noreferrer" class="scryerLinkBtn">' +
                    escapeHtml(p[0]) + '</a>';
            }).join('');

            modal.querySelector('.scryerModalLinks').innerHTML = html;
        }

        function renderRatings(item) {
            var ratings = Array.isArray(item.externalRatings) ? item.externalRatings : [];
            var html = ratings.map(function (r) {
                var display = r.value != null ? r.value
                    : (r.normalized != null ? r.normalized + '%' : (r.score != null ? r.score : ''));
                var inner = escapeHtml(r.source) + ' ' + escapeHtml(String(display));
                return r.url
                    ? '<a href="' + escapeHtml(r.url) + '" target="_blank" rel="noopener noreferrer" class="scryerRatingBadge">' + inner + '</a>'
                    : '<span class="scryerRatingBadge">' + inner + '</span>';
            }).join('');

            modal.querySelector('.scryerModalRatings').innerHTML = html;
        }

        // Root folders belong to a specific library (LibraryPayload.roots); the
        // standalone rootFolders(facet) query returns path/isDefault but no ID, and
        // Scryer's addTitle rejects a path used as rootFolderId. So this re-populates
        // from whichever library is currently selected.
        function populateRootFoldersForLibrary(rootFolderSelect, libraries, libraryId) {
            var lib = libraries.filter(function (l) { return l.id === libraryId; })[0];
            var roots = (lib && lib.roots) || [];
            var defaultRoot = roots.filter(function (r) { return r.isDefault; })[0] || roots[0];
            populateSelect(rootFolderSelect, roots, 'id', 'path', defaultRoot && defaultRoot.id);
        }

        function setupAdminForm(item) {
            var facet = facetOf(item);
            var librarySelect = modal.querySelector('.scryerLibrarySelect');
            var qualitySelect = modal.querySelector('.scryerQualitySelect');
            var rootFolderSelect = modal.querySelector('.scryerRootFolderSelect');

            return Promise.all([getLibraries(), getQualityProfiles()])
                .then(function (results) {
                    var libraries = results[0];
                    var profiles = results[1];

                    var facetLibraries = libraries.filter(function (l) { return l.facet === facet; });
                    var selectableLibraries = facetLibraries.length ? facetLibraries : libraries;
                    populateSelect(librarySelect, selectableLibraries, 'id', 'name');

                    var defaultProfileId = facetLibraries[0] && facetLibraries[0].qualityProfileId;
                    populateSelect(qualitySelect, profiles, 'id', 'name', defaultProfileId);

                    populateRootFoldersForLibrary(rootFolderSelect, libraries, librarySelect.value);
                    librarySelect.onchange = function () {
                        populateRootFoldersForLibrary(rootFolderSelect, libraries, librarySelect.value);
                    };
                });
        }

        function openModal(item) {
            renderPosterInto(modal.querySelector('.scryerModalPoster'), item.posterUrl);
            modal.querySelector('.scryerModalTitle').textContent = item.displayTitle || item.name || 'Untitled';
            modal.querySelector('.scryerModalYear').textContent = item.year || '';
            modal.querySelector('.scryerModalMessage').textContent = '';
            renderRatings(item);
            renderLinks(item);

            var overviewEl = modal.querySelector('.scryerModalOverview');
            var requestBtn = modal.querySelector('.scryerModalRequestBtn');
            var adminForm = modal.querySelector('.scryerModalAdminForm');
            requestBtn.disabled = false;

            var needsDetail = item.targetKey && !item._detailFetched;
            var detailPromise = needsDetail
                ? apiGet('Scryer/Discovery/Item?targetKey=' + encodeURIComponent(item.targetKey)).then(function (data) {
                    var detail = data.discoveryItemDetail;
                    item._detailFetched = true;
                    if (!detail) return;
                    item.overview = detail.overview || '';
                    item.externalIds = detail.externalIds || [];
                    item.externalRatings = detail.externalRatings || [];
                    if (!item.posterUrl && detail.posterUrl) {
                        item.posterUrl = detail.posterUrl;
                        renderPosterInto(modal.querySelector('.scryerModalPoster'), item.posterUrl);
                    }
                    renderRatings(item);
                    renderLinks(item);
                }).catch(function () {})
                : Promise.resolve();

            overviewEl.textContent = needsDetail ? 'Loading…' : (item.overview || '');
            detailPromise.then(function () { overviewEl.textContent = item.overview || ''; });

            isAdminUser().then(function (admin) {
                if (admin) {
                    requestBtn.textContent = 'Add to Catalog';
                    requestBtn.onclick = function () { submitAddToCatalog(item, requestBtn); };
                    adminForm.classList.remove('hide');
                    setupAdminForm(item).catch(function (err) {
                        modal.querySelector('.scryerModalMessage').textContent = err.message;
                    });
                } else {
                    requestBtn.textContent = 'Request';
                    requestBtn.onclick = function () { submitRequest(item, requestBtn); };
                    adminForm.classList.add('hide');
                }
            });

            backdrop.classList.remove('hide');
            modal.classList.remove('hide');
        }

        function closeModal() {
            backdrop.classList.add('hide');
            modal.classList.add('hide');
        }

        function submitRequest(item, button) {
            var facet = facetOf(item);
            var messageEl = modal.querySelector('.scryerModalMessage');
            button.disabled = true;
            button.textContent = 'Requesting…';
            messageEl.textContent = '';

            getLibraries().then(function (libraries) {
                var lib = libraries.filter(function (l) { return l.facet === facet; })[0];
                if (!lib) {
                    throw new Error('No library configured for ' + facet);
                }

                return apiPost('Scryer/Requests', {
                    LibraryId: lib.id,
                    Facet: facet,
                    Title: item.displayTitle || item.name,
                    Year: item.year || null,
                    ExternalIds: externalIdsFor(item)
                });
            }).then(function () {
                button.textContent = 'Requested';
                messageEl.textContent = 'Request submitted.';
                messageEl.className = 'scryerModalMessage scryerModalMessage-success';
            }).catch(function (err) {
                button.disabled = false;
                button.textContent = 'Request';
                messageEl.textContent = err.message;
                messageEl.className = 'scryerModalMessage scryerModalMessage-error';
            });
        }

        function submitAddToCatalog(item, button) {
            var facet = facetOf(item);
            var messageEl = modal.querySelector('.scryerModalMessage');
            var librarySelect = modal.querySelector('.scryerLibrarySelect');
            var qualitySelect = modal.querySelector('.scryerQualitySelect');
            var rootFolderSelect = modal.querySelector('.scryerRootFolderSelect');
            var monitoredCheckbox = modal.querySelector('.scryerMonitoredCheckbox');

            button.disabled = true;
            button.textContent = 'Adding…';
            messageEl.textContent = '';

            apiPost('Scryer/Catalog/Add', {
                Name: item.displayTitle || item.name,
                Facet: facet,
                LibraryId: librarySelect.value,
                Monitored: monitoredCheckbox.checked,
                ExternalIds: externalIdsFor(item),
                Year: item.year || null,
                Overview: item.overview || null,
                QualityProfileId: qualitySelect.value || null,
                RootFolderId: rootFolderSelect.value || null
            }).then(function () {
                button.textContent = 'Added';
                messageEl.textContent = 'Added to catalog. Monitored search will find a release automatically.';
                messageEl.className = 'scryerModalMessage scryerModalMessage-success';
            }).catch(function (err) {
                button.disabled = false;
                button.textContent = 'Add to Catalog';
                messageEl.textContent = err.message;
                messageEl.className = 'scryerModalMessage scryerModalMessage-error';
            });
        }

        categoriesEl.addEventListener('click', function (e) {
            var card = e.target.closest('.scryerCard');
            if (!card) return;
            var item = items[parseInt(card.dataset.index, 10)];
            if (item) openModal(item);
        });

        backdrop.addEventListener('click', closeModal);
        modal.querySelector('.scryerModalClose').addEventListener('click', closeModal);

        var searchTimer;
        container.querySelector('.scryerSearchBox').addEventListener('input', function (e) {
            clearTimeout(searchTimer);
            var q = e.target.value;
            if (!q) { loadTrending(); return; }

            searchTimer = setTimeout(function () {
                categoriesEl.innerHTML = LOADING_HTML;
                apiGet('Scryer/Discovery/Search?q=' + encodeURIComponent(q) + '&limit=25').then(function (data) {
                    var result = data.searchMetadataMulti || {};
                    var found = [].concat(
                        (result.movies || []).map(function (i) { return Object.assign({ targetKind: 'MOVIE' }, i); }),
                        (result.series || []).map(function (i) { return Object.assign({ targetKind: 'SERIES' }, i); }),
                        (result.anime || []).map(function (i) { return Object.assign({ targetKind: 'ANIME' }, i); })
                    );
                    renderCards(found);
                }).catch(function (err) { categoriesEl.innerHTML = '<p>' + escapeHtml(err.message) + '</p>'; });
            }, 300);
        });

        loadTrending();
    }

    Scryer.pages.discovery = renderDiscovery;
})();
