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
        function recommendationLookups(seed) {
            var ids = seed.providerIds || {};
            var lookups = [];
            function add(source, value) { if (value) lookups.push({ source: source, value: value }); }
            if (seed.kind === 'MOVIE') {
                add('tmdb', ids.tmdb); add('tmdb_movie', ids.tmdb);
                add('imdb', ids.imdb);
                add('tvdb', ids.tvdb); add('tvdb_movie', ids.tvdb);
            } else {
                add('tvdb', ids.tvdb); add('tvdb_series', ids.tvdb); add('tvdb_show', ids.tvdb);
                add('tmdb', ids.tmdb); add('tmdb_series', ids.tmdb); add('tmdb_tv', ids.tmdb); add('tmdb_show', ids.tmdb);
                add('imdb', ids.imdb);
            }
            return lookups;
        }
        function recommendationGroup(seed) {
            var lookups = recommendationLookups(seed);
            function tryLookup(index) {
                if (index >= lookups.length) return Promise.resolve(null);
                var lookup = lookups[index];
                return apiGet('Scryer/Discovery/MoreLikeThis?source=' + encodeURIComponent(lookup.source) + '&value=' + encodeURIComponent(lookup.value) + '&limit=20').then(function (data) {
                    var matches = data.recommendationTitles || [];
                    if (!matches.length) return tryLookup(index + 1);
                    var recommendations = matches[0].moreLikeThis || [];
                    return recommendations.length ? { title: 'More like ' + seed.title, items: recommendations } : null;
                });
            }
            return tryLookup(0);
        }
        function recentRecommendationGroups() {
            return Scryer.getRecentWatchSeeds(5).then(function (seeds) {
                return Promise.all(seeds.map(recommendationGroup));
            }).then(function (groups) {
                return groups.filter(function (group) { return !!group; }).slice(0, 5);
            }).catch(function () { return []; });
        }
        function uniqueGroups(groups) {
            var seen = {};
            return groups.map(function (group) {
                return { title: group.title, items: (group.items || []).filter(function (item) {
                    var key = item.targetKey || item.id;
                    if (!key || seen[key]) return false;
                    seen[key] = true;
                    return true;
                }) };
            }).filter(function (group) { return group.items.length > 0; });
        }
        function loadTrending() {
            categories.innerHTML = LOADING_HTML;
            var genericGroups = apiGet('Scryer/Discovery/Trending').then(function (data) {
                var payload = data.discoveryHomeCards || {};
                var groups = [];
                (payload.publicSections || []).forEach(function (section) { groups.push({ title: section.title, items: section.items }); });
                if (payload.canViewPersonalized) (payload.personalizedSections || []).forEach(function (section) { groups.push({ title: section.title, items: section.items }); });
                return { groups: groups, error: null };
            }, function (error) { return { groups: [], error: error }; });
            Promise.all([recentRecommendationGroups(), genericGroups]).then(scope.guard(function (results) {
                var groups = uniqueGroups(results[0].concat(results[1].groups));
                if (!groups.length && results[1].error) {
                    categories.innerHTML = '<p role="alert">' + escapeHtml(results[1].error.message) + '</p>';
                    return;
                }
                renderCategories(groups);
            }));
        }
        function externalIdsFor(item) {
            var ids = [];
            var seen = {};
            function add(source, value) {
                source = String(source || '').trim().toLowerCase();
                value = String(value || '').trim();
                var key = source + '\u001f' + value;
                if (!source || !value || seen[key]) return;
                seen[key] = true;
                ids.push({ Source: source, Value: value });
            }
            (item.externalIds || []).forEach(function (entry) { add(entry.source, entry.id || entry.value); });
            add('tmdb', item.tmdbId);
            add('tvdb', item.tvdbId);
            add('imdb', item.imdbId);
            if (!ids.length && item.targetKey) {
                var targetParts = String(item.targetKey).split(':');
                if (targetParts.length >= 2) add(targetParts[0], targetParts[targetParts.length - 1]);
            }
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
        function submitAdd(item, button, choices, isCurrentModal) {
            if (!isCurrentModal()) return;
            var facet = facetOf(item);
            var message = modal.querySelector('.scryerModalMessage');
            var librarySelect = modal.querySelector('.scryerRequestLibrary');
            var profileSelect = modal.querySelector('.scryerRequestQuality');
            var monitorSelect = modal.querySelector('.scryerRequestMonitor');
            var library = choices.libraries.filter(function (entry) { return entry.id === librarySelect.value; })[0];
            var externalIds = externalIdsFor(item);
            if (!library || !library._canManageTitles || !profileSelect.value || !monitorSelect.value || !externalIds.length) {
                message.textContent = 'Choose a manageable library, quality profile, and monitoring policy.';
                message.className = 'scryerModalMessage scryerModalMessage-error';
                return;
            }
            button.disabled = true;
            button.textContent = 'Adding…';
            message.textContent = '';
            apiPost('Scryer/Catalog/Titles', {
                LibraryId: library.id, Facet: facet, Title: item.displayTitle || item.name, Year: item.year || null,
                ExternalIds: externalIds, Overview: item.overview || null, SortTitle: item.sortTitle || null,
                Slug: item.slug || null, RuntimeMinutes: item.runtimeMinutes || null, Language: item.language || null,
                ContentStatus: item.status || null, QualityProfileId: profileSelect.value, MonitorType: monitorSelect.value
            }).then(scope.guard(function (data) {
                if (!isCurrentModal()) return;
                var result = data && data.addTitle;
                button.textContent = 'Added';
                message.textContent = result && result.reusedExistingTitle ? 'Already in Scryer.' : 'Added directly to Scryer.';
                message.className = 'scryerModalMessage scryerModalMessage-success';
            }), scope.guard(function (error) {
                if (!isCurrentModal()) return;
                button.disabled = false;
                button.textContent = 'Add to Scryer';
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
            var detailPromise = Promise.resolve(item);
            if (needsDetail) {
                detailPromise = apiGet('Scryer/Discovery/Item?targetKey=' + encodeURIComponent(item.targetKey)).then(scope.guard(function (data) {
                    if (!isCurrentModal()) return item;
                    var detail = data.discoveryItemDetail;
                    item._detailFetched = true;
                    if (!detail) return item;
                    item.overview = detail.overview || '';
                    item.externalIds = detail.externalIds || [];
                    item.externalRatings = detail.externalRatings || [];
                    if (!item.posterUrl && detail.posterUrl) { item.posterUrl = detail.posterUrl; renderPosterInto(modal.querySelector('.scryerModalPoster'), item.posterUrl, isCurrentModal); }
                    overview.textContent = item.overview;
                    renderRatings(item);
                    renderLinks(item);
                    return item;
                }));
            }
            var requestForm = modal.querySelector('.scryerModalAdminForm');
            requestForm.classList.add('hide');
            requestForm.innerHTML = '';
            request.disabled = true;
            request.textContent = 'Loading choices…';
            var facet = facetOf(item);
            Promise.all([
                detailPromise,
                apiGet('Scryer/Libraries?facet=' + encodeURIComponent(facet)),
                apiGet('Scryer/Libraries/Manageable?facet=' + encodeURIComponent(facet)),
                Scryer.getQualityProfiles()
            ]).then(scope.guard(function (results) {
                if (!isCurrentModal()) return;
                var byId = {};
                var libraries = [];
                function mergeLibraries(entries, permission) {
                    (entries || []).filter(function (library) { return library.facet === facet; }).forEach(function (library) {
                        var current = byId[library.id];
                        if (!current) {
                            current = byId[library.id] = Object.assign({}, library);
                            libraries.push(current);
                        } else {
                            Object.keys(library).forEach(function (key) { if (library[key] != null) current[key] = library[key]; });
                        }
                        current[permission] = true;
                    });
                }
                mergeLibraries(results[2].libraries, '_canManageTitles');
                mergeLibraries(results[1].libraries, '_canRequest');
                if (!libraries.length) {
                    request.disabled = true;
                    request.textContent = 'Unavailable';
                    modal.querySelector('.scryerModalMessage').textContent = 'Your Scryer account cannot add or request this title.';
                    return;
                }
                var profiles = results[3] || [];
                requestForm.classList.remove('hide');
                requestForm.innerHTML = '<label><span class="inputLabel">Library</span><select class="scryerRequestLibrary">' + libraries.map(function (library) { return '<option value="' + escapeHtml(library.id) + '">' + escapeHtml(library.name || library.slug || library.id) + ' — ' + (library._canManageTitles ? 'Add' : 'Request') + '</option>'; }).join('') + '</select></label><label><span class="inputLabel">Quality profile</span><select class="scryerRequestQuality"></select></label><label><span class="inputLabel">Monitoring</span><select class="scryerRequestMonitor">' + monitorOptionsHtml('MONITORED') + '</select></label>';
                var librarySelect = requestForm.querySelector('.scryerRequestLibrary');
                var qualitySelect = requestForm.querySelector('.scryerRequestQuality');
                function updateAction() {
                    if (!isCurrentModal()) return;
                    var selected = libraries.filter(function (library) { return library.id === librarySelect.value; })[0];
                    var choices = selected && selected._canManageTitles ? profiles : Scryer.ui.profilesForLibrary(profiles, selected);
                    qualitySelect.innerHTML = profilesHtml(choices, selected && (selected.qualityProfileId || selected.requestQualityProfileDefaultId));
                    request.disabled = !selected || !choices.length || !externalIdsFor(item).length;
                    request.textContent = selected && selected._canManageTitles ? 'Add to Scryer' : 'Request';
                    request.onclick = scope.guard(function () {
                        if (selected && selected._canManageTitles) submitAdd(item, request, { libraries: libraries }, isCurrentModal);
                        else submitRequest(item, request, { libraries: libraries }, isCurrentModal);
                    });
                    if (!externalIdsFor(item).length) modal.querySelector('.scryerModalMessage').textContent = 'This title has no supported external identifier.';
                }
                scope.on(librarySelect, 'change', updateAction);
                updateAction();
            }), scope.guard(function (error) {
                if (!isCurrentModal()) return;
                request.disabled = true;
                request.textContent = 'Unavailable';
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
