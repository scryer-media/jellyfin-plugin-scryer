(function () {
    'use strict';

    var Scryer = window.Scryer;
    var apiGet = Scryer.apiGet, apiPost = Scryer.apiPost, escapeHtml = Scryer.escapeHtml;
    var resolveImageUrl = Scryer.resolveImageUrl, LOADING_HTML = Scryer.LOADING_HTML;
    var getLibraries = Scryer.getLibraries, getCapabilities = Scryer.getCapabilities, facetOf = Scryer.facetOf;

    function renderDiscovery(container, scope) {
        container.innerHTML = '<h1>Discover</h1><div class="inputContainer"><input is="emby-input" type="text" class="scryerSearchBox" placeholder="Search movies and shows" /></div><div class="scryerTabGroup scryerDiscoveryFacetTabs" aria-label="Search result type"></div><div class="scryerCategories"></div>';
        var categories = container.querySelector('.scryerCategories');
        var facetTabs = container.querySelector('.scryerDiscoveryFacetTabs');
        var modalRefs = createModal(scope);
        var backdrop = modalRefs.backdrop;
        var modal = modalRefs.modal;
        var items = [];
        var searchTimer = null;
        var searchGeneration = 0;
        var activeFacet = 'ALL';
        var latestSearchResults = null;
        var lastFocused = null;
        var modalGate = Scryer.ui.createGenerationGate();
        scope.own(function () { if (searchTimer) window.clearTimeout(searchTimer); });
        scope.own(function () { modalGate.invalidate(); });

        function posterPlaceholderHtml() {
            return '<div class="scryerPosterPlaceholder"><svg class="scryerPlaceholderIcon" viewBox="0 0 24 24" focusable="false" aria-hidden="true"><path fill="currentColor" d="M18 4l2 4h-3l-2-4h-2l2 4h-3l-2-4H8l2 4H7L5 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V4h-4z"></path></svg></div>';
        }
        function renderPosterInto(element, url, isCurrent) {
            element.innerHTML = posterPlaceholderHtml();
            if (!url) return;
            resolveImageUrl(url).then(scope.guard(function (resolved) {
                if (isCurrent && !isCurrent()) return;
                var image = new Image();
                image.alt = '';
                image.onload = scope.guard(function () {
                    if (isCurrent && !isCurrent()) return;
                    element.innerHTML = ''; element.appendChild(image);
                });
                image.src = resolved;
            }));
        }
        function cardHtml(item) {
            return '<div class="scryerCardPoster"></div><div class="scryerCardTitle">' + escapeHtml(item.displayTitle || item.name || 'Untitled') + '</div>';
        }
        function renderCategories(groups) {
            items = [];
            categories.innerHTML = '';
            var visible = 0;
            groups.forEach(function (group) {
                if (!group.items || !group.items.length) return;
                visible++;
                var section = document.createElement('section');
                section.className = 'scryerCategory';
                section.innerHTML = '<h2 class="scryerCategoryTitle">' + escapeHtml(group.title) + '</h2><div class="scryerCarousel"><button type="button" class="scryerCarouselNav scryerCarouselPrev" aria-label="Previous">&lsaquo;</button><div class="scryerRow"></div><button type="button" class="scryerCarouselNav scryerCarouselNext" aria-label="Next">&rsaquo;</button></div>';
                categories.appendChild(section);
                var row = section.querySelector('.scryerRow');
                group.items.forEach(function (item) {
                    var index = items.length;
                    items.push(item);
                    var card = document.createElement('button');
                    card.type = 'button';
                    card.className = 'scryerCard';
                    card.dataset.index = index;
                    card.setAttribute('aria-label', item.displayTitle || item.name || 'Untitled');
                    card.innerHTML = cardHtml(item);
                    row.appendChild(card);
                    renderPosterInto(card.querySelector('.scryerCardPoster'), item.posterUrl);
                });
                scope.on(section.querySelector('.scryerCarouselPrev'), 'click', function () { row.scrollBy({ left: -row.clientWidth * 0.9, behavior: 'smooth' }); });
                scope.on(section.querySelector('.scryerCarouselNext'), 'click', function () { row.scrollBy({ left: row.clientWidth * 0.9, behavior: 'smooth' }); });
            });
            if (!visible) categories.innerHTML = '<p role="status">No matching titles were found.</p>';
        }
        function renderFacetTabs() {
            facetTabs.innerHTML = ['ALL', 'MOVIE', 'SERIES', 'ANIME'].map(function (facet) {
                var label = facet === 'ALL' ? 'All' : facet.charAt(0) + facet.slice(1).toLowerCase();
                return '<button type="button" class="scryerTab' + (facet === activeFacet ? ' scryerTabActive' : '') + '" data-facet="' + facet + '">' + label + '</button>';
            }).join('');
        }
        function renderCards(found) {
            latestSearchResults = found;
            renderFacetTabs();
            renderCategories([{ title: 'Results', items: found.filter(function (item) { return activeFacet === 'ALL' || facetOf(item) === activeFacet; }) }]);
        }
        function loadTrending() {
            categories.innerHTML = LOADING_HTML;
            apiGet('Scryer/Discovery/Trending').then(scope.guard(function (data) {
                var payload = data.discoveryHomeCards || {};
                var groups = [];
                (payload.publicSections || []).forEach(function (section) { groups.push({ title: section.title, items: section.items }); });
                if (payload.canViewPersonalized) (payload.personalizedSections || []).forEach(function (section) { groups.push({ title: section.title, items: section.items }); });
                renderCategories(groups);
            }), scope.guard(function (error) { categories.innerHTML = '<p role="alert">' + escapeHtml(error.message) + '</p>'; }));
        }
        function externalIdsFor(item) {
            if (Array.isArray(item.externalIds) && item.externalIds.length) return item.externalIds.map(function (entry) { return { Source: entry.source, Value: String(entry.id || entry.value) }; });
            var ids = [];
            if (item.tmdbId) ids.push({ Source: 'tmdb', Value: String(item.tmdbId) });
            if (item.tvdbId) ids.push({ Source: 'tvdb', Value: String(item.tvdbId) });
            return ids;
        }
        function externalLinkUrl(source, id, facet) {
            var normalized = (source || '').toLowerCase();
            if (!id) return null;
            if (normalized === 'imdb') return 'https://www.imdb.com/title/' + id + '/';
            if (normalized === 'tmdb') return 'https://www.themoviedb.org/' + (facet === 'MOVIE' ? 'movie' : 'tv') + '/' + id;
            if (normalized === 'tvdb') return 'https://www.thetvdb.com/dereferrer/' + (facet === 'MOVIE' ? 'movie' : 'series') + '/' + id;
            return null;
        }
        function renderLinks(item) {
            var pairs = [];
            (item.externalIds || []).forEach(function (entry) { pairs.push([entry.source, entry.id || entry.value]); });
            modal.querySelector('.scryerModalLinks').innerHTML = pairs.map(function (pair) {
                var url = externalLinkUrl(pair[0], pair[1], facetOf(item));
                return url ? '<a href="' + escapeHtml(url) + '" target="_blank" rel="noopener noreferrer" class="scryerLinkBtn">' + escapeHtml(pair[0]) + '</a>' : '';
            }).join('');
        }
        function renderRatings(item) {
            modal.querySelector('.scryerModalRatings').innerHTML = (item.externalRatings || []).map(function (rating) {
                var value = rating.value != null ? rating.value : (rating.normalized != null ? rating.normalized + '%' : (rating.score != null ? rating.score : ''));
                var text = escapeHtml(rating.source) + ' ' + escapeHtml(String(value));
                var url = null;
                try {
                    var parsed = new URL(rating.url);
                    if (parsed.protocol === 'http:' || parsed.protocol === 'https:') url = parsed.href;
                } catch (error) {}
                return url ? '<a href="' + escapeHtml(url) + '" target="_blank" rel="noopener noreferrer" class="scryerRatingBadge">' + text + '</a>' : '<span class="scryerRatingBadge">' + text + '</span>';
            }).join('');
        }
        function closeModal() {
            modalGate.invalidate();
            modal.querySelector('.scryerModalRequestBtn').onclick = null;
            backdrop.classList.add('hide');
            modal.classList.add('hide');
            if (lastFocused && lastFocused.focus) lastFocused.focus();
        }
        function monitorOptionsHtml(selected) {
            return Scryer.ui.monitorOptions.map(function (option) {
                return '<option value="' + option.value + '"' + (option.value === selected ? ' selected' : '') + '>' + escapeHtml(option.label) + '</option>';
            }).join('');
        }
        function profilesHtml(profiles, selected) {
            return profiles.map(function (profile) {
                return '<option value="' + escapeHtml(profile.id) + '"' + (profile.id === selected ? ' selected' : '') + '>' + escapeHtml(profile.name) + '</option>';
            }).join('');
        }
        function submitRequest(item, button, choices, isCurrentModal) {
            if (!isCurrentModal()) return;
            var facet = facetOf(item);
            var message = modal.querySelector('.scryerModalMessage');
            var librarySelect = modal.querySelector('.scryerRequestLibrary');
            var profileSelect = modal.querySelector('.scryerRequestQuality');
            var monitorSelect = modal.querySelector('.scryerRequestMonitor');
            var library = choices.libraries.filter(function (entry) { return entry.id === librarySelect.value; })[0];
            if (!library || !profileSelect.value || !monitorSelect.value) {
                message.textContent = 'Choose a library, quality profile, and monitoring policy.';
                message.className = 'scryerModalMessage scryerModalMessage-error';
                return;
            }
            button.disabled = true;
            button.textContent = 'Requesting…';
            message.textContent = '';
            apiPost('Scryer/Requests', {
                LibraryId: library.id, Facet: facet, Title: item.displayTitle || item.name, Year: item.year || null,
                ExternalIds: externalIdsFor(item), Overview: item.overview || null, SortTitle: item.sortTitle || null,
                Slug: item.slug || null, RuntimeMinutes: item.runtimeMinutes || null, Language: item.language || null,
                ContentStatus: item.status || null, RequestedQualityProfileId: profileSelect.value, RequestedMonitorType: monitorSelect.value
            }).then(scope.guard(function (data) {
                if (!isCurrentModal()) return;
                button.textContent = 'Requested';
                var requestId = data && data.submitMediaRequest && data.submitMediaRequest.requestId;
                message.textContent = requestId ? 'Pending request submitted.' : 'Request submitted.';
                message.className = 'scryerModalMessage scryerModalMessage-success';
            }), scope.guard(function (error) {
                if (!isCurrentModal()) return;
                button.disabled = false;
                button.textContent = 'Request';
                message.textContent = error.message;
                message.className = 'scryerModalMessage scryerModalMessage-error';
            }));
        }
        function openModal(item) {
            var modalToken = modalGate.begin();
            var isCurrentModal = function () { return scope.isCurrent() && modalGate.isCurrent(modalToken); };
            renderPosterInto(modal.querySelector('.scryerModalPoster'), item.posterUrl, isCurrentModal);
            modal.querySelector('.scryerModalTitle').textContent = item.displayTitle || item.name || 'Untitled';
            modal.querySelector('.scryerModalYear').textContent = item.year || '';
            modal.querySelector('.scryerModalMessage').textContent = '';
            renderRatings(item);
            renderLinks(item);
            var overview = modal.querySelector('.scryerModalOverview');
            var request = modal.querySelector('.scryerModalRequestBtn');
            request.onclick = null;
            request.disabled = false;
            var needsDetail = item.targetKey && !item._detailFetched;
            overview.textContent = needsDetail ? 'Loading…' : (item.overview || '');
            if (needsDetail) {
                apiGet('Scryer/Discovery/Item?targetKey=' + encodeURIComponent(item.targetKey)).then(scope.guard(function (data) {
                    if (!isCurrentModal()) return;
                    var detail = data.discoveryItemDetail;
                    item._detailFetched = true;
                    if (!detail) return;
                    item.overview = detail.overview || '';
                    item.externalIds = detail.externalIds || [];
                    item.externalRatings = detail.externalRatings || [];
                    if (!item.posterUrl && detail.posterUrl) { item.posterUrl = detail.posterUrl; renderPosterInto(modal.querySelector('.scryerModalPoster'), item.posterUrl, isCurrentModal); }
                    overview.textContent = item.overview;
                    renderRatings(item);
                    renderLinks(item);
                }));
            }
            var requestForm = modal.querySelector('.scryerModalAdminForm');
            requestForm.classList.add('hide');
            requestForm.innerHTML = '';
            request.disabled = true;
            request.textContent = 'Loading request choices…';
            Promise.all([apiGet('Scryer/Libraries?facet=' + encodeURIComponent(facetOf(item))), Scryer.getQualityProfiles()]).then(scope.guard(function (results) {
                if (!isCurrentModal()) return;
                var libraries = (results[0].libraries || []).filter(function (library) { return library.facet === facetOf(item); });
                if (!libraries.length) {
                    request.disabled = true;
                    request.textContent = 'Request unavailable';
                    modal.querySelector('.scryerModalMessage').textContent = 'Your Scryer account cannot request this title.';
                    return;
                }
                var profiles = results[1] || [];
                var availableProfiles = Scryer.ui.profilesForLibrary(profiles, libraries[0]);
                if (!availableProfiles.length) {
                    request.disabled = true;
                    request.textContent = 'Request unavailable';
                    modal.querySelector('.scryerModalMessage').textContent = 'This library has no requestable quality profile.';
                    return;
                }
                requestForm.classList.remove('hide');
                requestForm.innerHTML = '<label><span class="inputLabel">Library</span><select class="scryerRequestLibrary">' + libraries.map(function (library) { return '<option value="' + escapeHtml(library.id) + '">' + escapeHtml(library.name || library.slug || library.id) + '</option>'; }).join('') + '</select></label><label><span class="inputLabel">Quality profile</span><select class="scryerRequestQuality">' + profilesHtml(availableProfiles, libraries[0].qualityProfileId) + '</select></label><label><span class="inputLabel">Monitoring</span><select class="scryerRequestMonitor">' + monitorOptionsHtml('MONITORED') + '</select></label>';
                var librarySelect = requestForm.querySelector('.scryerRequestLibrary');
                var qualitySelect = requestForm.querySelector('.scryerRequestQuality');
                scope.on(librarySelect, 'change', function () {
                    if (!isCurrentModal()) return;
                    var selected = libraries.filter(function (library) { return library.id === librarySelect.value; })[0];
                    var choices = Scryer.ui.profilesForLibrary(profiles, selected);
                    qualitySelect.innerHTML = profilesHtml(choices, selected && selected.qualityProfileId);
                    request.disabled = !choices.length;
                });
                request.disabled = false;
                request.textContent = 'Request';
                request.onclick = scope.guard(function () { submitRequest(item, request, { libraries: libraries }, isCurrentModal); });
            }), scope.guard(function (error) {
                if (!isCurrentModal()) return;
                request.disabled = true;
                request.textContent = 'Request unavailable';
                modal.querySelector('.scryerModalMessage').textContent = error.message;
            }));
            lastFocused = document.activeElement;
            backdrop.classList.remove('hide');
            modal.classList.remove('hide');
            modal.querySelector('.scryerModalClose').focus();
        }

        scope.on(categories, 'click', function (event) {
            var card = event.target.closest('.scryerCard');
            if (!card) return;
            var item = items[parseInt(card.dataset.index, 10)];
            if (item) openModal(item);
        });
        scope.on(facetTabs, 'click', function (event) {
            var button = event.target.closest('button[data-facet]');
            if (!button || !latestSearchResults) return;
            activeFacet = button.dataset.facet;
            renderCards(latestSearchResults);
        });
        scope.on(backdrop, 'click', closeModal);
        scope.on(modal.querySelector('.scryerModalClose'), 'click', closeModal);
        scope.on(document, 'keydown', function (event) {
            if (modal.classList.contains('hide')) return;
            if (event.key === 'Escape') { event.preventDefault(); closeModal(); return; }
            if (event.key !== 'Tab') return;
            var focusable = modal.querySelectorAll('button:not([disabled]), select:not([disabled]), a[href], input:not([disabled])');
            if (!focusable.length) return;
            var first = focusable[0];
            var last = focusable[focusable.length - 1];
            if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
            if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
        });
        scope.on(container.querySelector('.scryerSearchBox'), 'input', function (event) {
            if (searchTimer) window.clearTimeout(searchTimer);
            var query = event.target.value;
            var requestGeneration = ++searchGeneration;
            if (!query) { loadTrending(); return; }
            searchTimer = window.setTimeout(scope.guard(function () {
                categories.innerHTML = LOADING_HTML;
                apiGet('Scryer/Discovery/Search?q=' + encodeURIComponent(query) + '&limit=25').then(scope.guard(function (data) {
                    if (requestGeneration !== searchGeneration) return;
                    var result = data.searchMetadataMulti || {};
                    var found = [].concat(
                        (result.movies || []).map(function (item) { return Object.assign({ targetKind: 'MOVIE' }, item); }),
                        (result.series || []).map(function (item) { return Object.assign({ targetKind: 'SERIES' }, item); }),
                        (result.anime || []).map(function (item) { return Object.assign({ targetKind: 'ANIME' }, item); })
                    );
                    renderCards(found);
                }), scope.guard(function (error) { if (requestGeneration === searchGeneration) categories.innerHTML = '<p role="alert">' + escapeHtml(error.message) + '</p>'; }));
            }), 300);
        });
        loadTrending();
        return closeModal;
    }

    function createModal(scope) {
        document.querySelectorAll('#scryer-discovery-modal, #scryer-discovery-backdrop').forEach(function (element) { element.remove(); });
        var backdrop = document.createElement('div');
        backdrop.id = 'scryer-discovery-backdrop';
        backdrop.className = 'scryerModalBackdrop dialogBackdrop dialogBackdropOpened hide scryer-runtime-owned';
        var modal = document.createElement('div');
        modal.id = 'scryer-discovery-modal';
        modal.className = 'scryerModal dialog hide scryer-runtime-owned';
        modal.setAttribute('role', 'dialog');
        modal.setAttribute('aria-modal', 'true');
        modal.setAttribute('aria-label', 'Scryer title details');
        modal.innerHTML = '<button type="button" class="scryerModalClose" aria-label="Close">&times;</button><div class="scryerModalPoster"></div><div class="scryerModalBody"><h2 class="scryerModalTitle"></h2><div class="scryerModalYear"></div><div class="scryerModalRatings"></div><p class="scryerModalOverview"></p><div class="scryerModalLinks"></div><div class="scryerModalAdminForm hide"></div><div class="scryerModalMessage" role="status" aria-live="polite"></div><button is="emby-button" type="button" class="raised scryerModalRequestBtn">Request</button></div>';
        backdrop.style.zIndex = '2147483000';
        modal.style.zIndex = '2147483001';
        document.body.appendChild(backdrop);
        document.body.appendChild(modal);
        scope.own(function () { backdrop.remove(); modal.remove(); });
        return { backdrop: backdrop, modal: modal };
    }

    Scryer.lifecycle.registerFeature('discovery', function (container, scope, context) {
        Scryer.withConnectionGate(container, scope, context.page, renderDiscovery);
    });
})();
