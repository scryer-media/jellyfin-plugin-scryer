/*
 * Scryer's injected CSS. Kept minimal on purpose: layout/spacing only where
 * needed, colors/typography come from the active Jellyfin theme (or the
 * server's custom CSS) via Jellyfin's own classes (.dialog, .navMenuOption,
 * .raised, etc.) rather than anything hardcoded here.
 */
(function () {
    'use strict';

    var STYLE = '' +
        '.scryerCategory{margin-top:1.8em}' +
        '.scryerCategoryTitle{margin:0 0 .3em;font-size:1.1em}' +
        '.scryerCarousel{position:relative}' +
        '.scryerRow{display:flex;gap:1.2em;overflow-x:auto;scroll-behavior:smooth;scrollbar-width:none;padding:.2em 0}' +
        '.scryerRow::-webkit-scrollbar{display:none}' +
        '.scryerCard{cursor:pointer;flex:0 0 160px}' +
        '.scryerCarouselNav{position:absolute;top:0;bottom:0;width:2.5em;border:none;background:linear-gradient(rgba(0,0,0,.55),rgba(0,0,0,.55));color:#fff;font-size:1.8em;cursor:pointer;opacity:0;transition:opacity .15s;z-index:2}' +
        '.scryerCarouselNav:hover{opacity:1}' +
        '.scryerCarouselPrev{left:0}' +
        '.scryerCarouselNext{right:0}' +
        '.scryerCardPoster img{width:100%;border-radius:6px;display:block;aspect-ratio:2/3;object-fit:cover}' +
        '.scryerCardTitle{margin-top:.4em;font-size:.9em}' +
        '.scryerPosterPlaceholder{width:100%;aspect-ratio:2/3;border-radius:6px;background:rgba(255,255,255,.08);display:flex;align-items:center;justify-content:center;color:rgba(255,255,255,.35)}' +
        '.scryerPosterPlaceholder .material-icons{font-size:2.5em}' +
        '.scryerRow{display:flex;align-items:center;justify-content:space-between;padding:.6em 0;border-bottom:1px solid rgba(255,255,255,.1)}' +
        '.scryerStatus-PENDING{color:#e5a00d}.scryerStatus-APPROVED{color:#4caf50}' +
        '.scryerStatus-REJECTED{color:#f44336}.scryerStatus-CANCELED{color:#888}' +
        '.scryerLoading{display:flex;align-items:center;justify-content:center;min-height:50vh;width:100%}' +
        '.scryerSpinner{width:2.5em;height:2.5em;border-radius:50%;border:.25em solid rgba(255,255,255,.15);border-top-color:#00a4dc;animation:scryerSpin 1s linear infinite}' +
        '@keyframes scryerSpin{to{transform:rotate(360deg)}}' +
        // Background/opacity come from Jellyfin's own .dialogBackdrop.dialogBackdropOpened
        // classes (including any theme/custom-CSS override of them); only layout here.
        '.scryerModalBackdrop{position:fixed;inset:0;z-index:10001}' +
        '.scryerModalBackdrop.hide{display:none}' +
        // Background/text color come from Jellyfin's own .dialog class; only layout/centering
        // here -- centered independently since it's a sibling of the backdrop, not a flex child.
        '.scryerModal{position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);display:flex;gap:1.5em;max-width:44em;width:90%;max-height:85vh;overflow-y:auto;padding:1.5em;z-index:10002}' +
        '.scryerModal.hide{display:none}' +
        '.scryerModalPoster{flex:0 0 10em}' +
        // Shadow so the poster's own edge doesn't disappear into the dialog's near-black background.
        '.scryerModalPoster img,.scryerModalPoster .scryerPosterPlaceholder{width:10em;border-radius:6px;box-shadow:0 4px 18px rgba(0,0,0,.7),0 0 0 1px rgba(255,255,255,.08)}' +
        '.scryerModalBody{flex:1;min-width:0}' +
        // .dialog's inherited text color is ~80% white; bumped for contrast on top of the blur/dark backdrop.
        '.scryerModalTitle{margin:0 0 .2em;color:#fff}' +
        '.scryerModalYear{color:rgba(255,255,255,.65);margin-bottom:.8em}' +
        '.scryerModalOverview{color:rgba(255,255,255,.92);line-height:1.5}' +
        '.scryerModalMessage{margin:.8em 0;min-height:1.2em}' +
        '.scryerModalMessage-success{color:#4caf50}.scryerModalMessage-error{color:#f44336}' +
        '.scryerModalClose{position:absolute;top:.5em;right:.5em;background:none;border:none;color:inherit;font-size:1.5em;line-height:1;cursor:pointer;opacity:.7}' +
        '.scryerModalClose:hover{opacity:1}' +
        '.scryerModalRatings{display:flex;flex-wrap:wrap;gap:.5em;margin:.5em 0}' +
        '.scryerRatingBadge{display:inline-flex;align-items:center;gap:.3em;padding:.3em .7em;border-radius:1em;background:rgba(255,255,255,.1);font-size:.85em;color:rgba(255,255,255,.92);text-decoration:none}' +
        '.scryerRatingBadge:hover{background:rgba(255,255,255,.18)}' +
        '.scryerModalLinks{display:flex;flex-wrap:wrap;gap:.5em;margin:.8em 0}' +
        '.scryerLinkBtn{padding:.4em .9em;border-radius:.4em;background:rgba(255,255,255,.1);color:rgba(255,255,255,.92);text-decoration:none;font-size:.85em;text-transform:uppercase;letter-spacing:.03em}' +
        '.scryerLinkBtn:hover{background:rgba(255,255,255,.18)}' +
        '.scryerModalAdminForm{margin:1em 0;display:flex;flex-direction:column;gap:.8em}' +
        '.scryerModalAdminForm.hide{display:none}' +
        '.scryerModalAdminForm .inputLabel{display:block;font-size:.85em;opacity:.75;margin-bottom:.3em}' +
        '.scryerModalAdminForm select{width:100%}' +
        '.scryerMonitoredRow{display:flex;align-items:center;gap:.6em;cursor:pointer}' +
        '.scryerBanner{display:flex;align-items:center;justify-content:space-between;gap:1em;margin:1em 0;padding:.8em 1em;border-radius:.5em}' +
        '.scryerBanner.hide{display:none}' +
        '.scryerBannerWarning{background:rgba(229,160,13,.15);border:1px solid rgba(229,160,13,.4)}' +
        '.scryerBannerClose{background:none;border:none;color:inherit;font-size:1.3em;cursor:pointer;opacity:.7}' +
        '.scryerBannerClose:hover{opacity:1}' +
        '.scryerConnectCard{box-sizing:border-box;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:1.35em;width:min(38em,calc(100% - 2em));min-height:24em;margin:3em auto;padding:2.5em 2em;border:1px solid rgba(255,255,255,.12);border-radius:1.1em;background:rgba(8,14,30,.78);box-shadow:0 1.2em 3em rgba(0,0,0,.35);text-align:center}' +
        '.scryerConnectBrands{display:flex;align-items:center;justify-content:center;gap:1.4em;width:100%}' +
        '.scryerConnectLogo{display:block;width:7.5em;height:7.5em;flex:0 0 7.5em}' +
        '.scryerConnectScryerLogo{object-fit:contain}' +
        '.scryerConnectJellyfinLogo{object-fit:contain}' +
        '.scryerConnectArrows{width:4.4em;height:2.8em;flex:0 1 4.4em;fill:none;stroke:#fff;stroke-width:4;stroke-linecap:round;stroke-linejoin:round;opacity:.88}' +
        '.scryerConnectMessage{max-width:30em;margin:0;font-size:1.15em;line-height:1.5}' +
        '.scryerConnectCard .scryerConnectionActions{width:min(22em,100%)}' +
        '.scryerConnectButton{box-sizing:border-box;width:100%;min-height:3.4em;border:0;border-radius:.55em;background:linear-gradient(100deg,#355baa,#de2189);color:#fff;font-size:1.15em;font-weight:700;letter-spacing:.02em;cursor:pointer}' +
        '.scryerConnectButton:hover{filter:brightness(1.12)}.scryerConnectButton:disabled{cursor:wait;filter:saturate(.45);opacity:.72}' +
        '@media(max-width:34em){.scryerConnectCard{min-height:20em;margin:1.5em auto;padding:2em 1.25em}.scryerConnectBrands{gap:.75em}.scryerConnectLogo{width:5.7em;height:5.7em;flex-basis:5.7em}.scryerConnectArrows{width:3.1em;flex-basis:3.1em}}' +
        '.scryerRequestTabs{display:flex;align-items:center;flex-wrap:wrap;gap:1.5em;margin:1em 0 1.2em;border-bottom:1px solid rgba(255,255,255,.1);padding-bottom:.8em}' +
        '.scryerTabGroup{display:flex;gap:1.2em}' +
        '.scryerDownloadTabs{margin:1em 0 1.2em;border-bottom:1px solid rgba(255,255,255,.1);padding-bottom:.8em}' +
        '.scryerTab{background:none;border:none;color:rgba(255,255,255,.6);font-weight:600;cursor:pointer;padding:.2em 0;border-bottom:2px solid transparent;display:flex;align-items:center;gap:.4em}' +
        '.scryerTab:hover{color:#fff}' +
        '.scryerTabActive{color:#fff;border-bottom-color:#00a4dc}' +
        '.scryerTabCount{background:rgba(255,255,255,.12);border-radius:1em;padding:.1em .6em;font-size:.8em;font-weight:700}' +
        '.scryerLibraryFilter{margin-left:auto}' +
        '.scryerLibraryFilter.hide{display:none}' +
        '.scryerDownloadList{display:flex;flex-direction:column;gap:1em;margin-top:1em}' +
        '.scryerDownloadItem{padding:.9em 1em;border-radius:.5em;background:rgba(255,255,255,.05)}' +
        '.scryerDownloadHeader{display:flex;align-items:center;justify-content:space-between;gap:1em;margin-bottom:.5em}' +
        '.scryerDownloadTitle{font-weight:600}' +
        '.scryerDownloadState{font-size:.8em;text-transform:uppercase;letter-spacing:.03em;opacity:.75;white-space:nowrap}' +
        '.scryerDownloadState-COMPLETED,.scryerDownloadState-IMPORTED_SEEDING{color:#4caf50}' +
        '.scryerDownloadState-FAILED,.scryerDownloadState-IMPORT_FAILED,.scryerDownloadState-REMOVE_FAILED{color:#f44336}' +
        '.scryerDownloadState-WARNING{color:#e5a00d}' +
        '.scryerDownloadProgressTrack{height:.4em;border-radius:1em;background:rgba(255,255,255,.1);overflow:hidden}' +
        '.scryerDownloadProgressFill{height:100%;background:#00a4dc;transition:width .3s}' +
        '.scryerDownloadMeta{display:flex;gap:.8em;margin-top:.4em;font-size:.85em;opacity:.7}' +
        '.scryerDownloadAttention{margin-top:.5em;font-size:.85em;color:#e5a00d}' +
        '.scryerCalendarGrid{margin-top:1em;display:grid;grid-template-columns:repeat(auto-fill,minmax(11em,1fr));gap:1.2em}' +
        '.scryerCalendarCard{padding:.6em;border-radius:.5em;background:rgba(255,255,255,.05)}' +
        '.scryerCalendarPoster{margin-bottom:.5em}' +
        '.scryerCalendarPoster img,.scryerCalendarPoster .scryerPosterPlaceholder{width:100%;border-radius:4px;aspect-ratio:2/3;object-fit:cover;display:block}' +
        '.scryerCalendarCardDate{font-size:.75em;opacity:.6;text-transform:uppercase;letter-spacing:.03em}' +
        '.scryerCalendarCardTitle{font-weight:600;overflow:hidden;text-overflow:ellipsis;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical}' +
        '.scryerCalendarCardEpisode{font-size:.85em;opacity:.7}' +
        '.scryerCalendarBadge{display:inline-block;margin-top:.3em;padding:.1em .6em;border-radius:1em;font-size:.75em;background:rgba(255,255,255,.1)}' +
        '.scryerCalendarBadge-AVAILABLE{color:#4caf50}' +
        '.scryerCalendarBadge-MISSING,.scryerCalendarBadge-SCAN_FAILED{color:#f44336}' +
        '.scryerCalendarBadge-UNMONITORED{color:#888}';

    if (!document.getElementById('scryer-style')) {
        var style = document.createElement('style');
        style.id = 'scryer-style';
        style.textContent = STYLE;
        document.head.appendChild(style);
    }
})();
