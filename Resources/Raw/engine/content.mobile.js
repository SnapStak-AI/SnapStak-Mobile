// content.mobile.js — SnapStak Mobile Extractor
// WebView-only version of content.js. Clean, minimal, no Chrome extension APIs.
//
// REMOVED:
//   - chrome.runtime message listener
//   - waitForImages beam scan + scroll (replaced with settle wait)
//   - extractPage, extractElement, extractHiddenForSegment
//   - extractSegmentBehaviour, serializeHiddenComponents
//   - ComponentSelector
//   - Visual centering translateX
//
// KEPT:
//   - prefetchSVGSprites
//   - stampSegmentIds
//   - expandHorizontalCarousels
//   - extractMobile  (entry point)
//   - serializeDOM

'use strict';

window.__snapstak_mobile_loaded__ = true;

// ── waitForImages (mobile) ────────────────────────────────────────────────────
// Scrolls the page down to bottom and back up to trigger lazy loading,
// IntersectionObserver callbacks, and dynamic content rendering.
// Then resolves any still-empty img src from currentSrc / srcset / data-src.
// This is the correct mobile WebView approach — window.scrollTo() scrolls
// the web page content (not the native View container).
async function waitForImages() {
    // ── Find scrollable container ─────────────────────────────────────────────
    function findScroller() {
        const root = document.scrollingElement || document.documentElement;
        if (root.scrollHeight > root.clientHeight + 10) return root;
        const all = Array.from(document.querySelectorAll('*'));
        for (const el of all) {
            const st = window.getComputedStyle(el);
            const ov = st.overflow + st.overflowY;
            if ((ov.includes('scroll') || ov.includes('auto'))
                && el.scrollHeight > el.clientHeight + 10) return el;
        }
        return document.documentElement;
    }

    const scroller = findScroller();
    const isRoot   = scroller === document.documentElement
                  || scroller === document.body
                  || scroller === document.scrollingElement;

    function doScroll(y) {
        if (isRoot) window.scrollTo(0, y);
        else scroller.scrollTop = y;
    }

    const vh   = window.innerHeight || 667;
    const step = Math.round(vh * 0.8);

    // ── Scroll DOWN — triggers IntersectionObserver + lazy loads ──────────────
    let pos   = 0;
    let pageH = Math.max(
        document.body.scrollHeight,
        document.documentElement.scrollHeight,
        scroller.scrollHeight
    );
    console.log(`[SnapStak] waitForImages: scrolling ${pageH}px`);

    while (pos < pageH) {
        pos = Math.min(pos + step, pageH);
        doScroll(pos);
        await new Promise(r => setTimeout(r, 400));
        const newH = Math.max(
            document.body.scrollHeight,
            document.documentElement.scrollHeight,
            scroller.scrollHeight
        );
        if (newH > pageH) { pageH = newH; }
    }

    // Settle at bottom
    await new Promise(r => setTimeout(r, 1000));

    // Store full page dimensions NOW — at the bottom before React collapses the DOM.
    // After scrolling back to top, React virtual lists remove off-screen nodes
    // and scrollHeight shrinks back to viewport height.
    window.__snapstak_full_page_height__ = Math.max(
        document.body.scrollHeight,
        document.documentElement.scrollHeight,
        scroller.scrollHeight
    );
    window.__snapstak_full_page_width__ = Math.max(
        document.body.scrollWidth,
        document.documentElement.scrollWidth
    );
    console.log(`[SnapStak] Full page: ${window.__snapstak_full_page_width__}x${window.__snapstak_full_page_height__}px`);

    // ── Resolve lazy image src ────────────────────────────────────────────────
    function pickSrcset(ss) {
        return (ss || '').split(',').map(p => p.trim().split(/\s+/)[0]).filter(Boolean)[0] || '';
    }
    let resolved = 0;
    for (const img of document.querySelectorAll('img')) {
        // Priority: CDN URL > currentSrc > srcset > data-src > keep existing
        // Even if img.src is a base64 LQIP, replace it with the real CDN URL
        // so the SVG <image> element renders the full-quality image.

        // 1. currentSrc — browser resolved after IntersectionObserver fired
        if (img.currentSrc && !img.currentSrc.startsWith('data:')) {
            img.src = img.currentSrc; resolved++; continue;
        }

        // 2. srcset directly on img
        const imgSS = img.getAttribute('srcset') || img.getAttribute('data-srcset') || '';
        if (imgSS) {
            const u = pickSrcset(imgSS);
            if (u && !u.startsWith('data:')) { img.src = u; resolved++; continue; }
        }

        // 3. <source srcset> inside parent <picture> — primary case for React apps
        //    <picture><source srcset="cdn.../img.jpg 320w, ..."><img src="lqip.jpg"></picture>
        const pic = img.closest('picture');
        if (pic) {
            const source = pic.querySelector('source[srcset]');
            if (source) {
                const u = pickSrcset(source.getAttribute('srcset') || '');
                if (u && !u.startsWith('data:')) { img.src = u; resolved++; continue; }
            }
        }

        // 4. data-src / data-lazy-src
        const ds = img.getAttribute('data-src') || img.getAttribute('data-lazy-src') || img.getAttribute('data-original') || '';
        if (ds && !ds.startsWith('data:')) { img.src = ds; resolved++; }
    }
    if (resolved > 0) {
        console.log(`[SnapStak] Resolved ${resolved} lazy image src(s)`);
        const loading = Array.from(document.querySelectorAll('img')).filter(img => img.src && !img.complete);
        if (loading.length > 0) {
            await Promise.race([
                Promise.all(loading.map(img => new Promise(r => { img.addEventListener('load', r); img.addEventListener('error', r); }))),
                new Promise(r => setTimeout(r, 4000)),
            ]);
        }
    }

    // ── Propagate resolved img src to container elements ────────────────────────
    // serializeDOM serializes the DOM tree. img is a VOID_TAG so it creates a
    // leaf entry with src. But the ancestor container (the absolute-position div
    // that wraps the picture) may have no src of its own and render as empty.
    // Fix: stamp data-snapstak-img-src on the nearest block ancestor with real
    // dimensions so serializeDOM picks it up as the container's image source.
    for (const img of document.querySelectorAll('img')) {
        const resolvedSrc = img.src || img.currentSrc || '';
        if (!resolvedSrc || resolvedSrc.startsWith('data:')) continue;
        // Walk up to find the nearest block container with real dimensions
        let ancestor = img.parentElement;
        while (ancestor && ancestor !== document.body) {
            const r = ancestor.getBoundingClientRect();
            if (r.width > 10 && r.height > 10) {
                const cs = window.getComputedStyle(ancestor);
                const disp = cs.display;
                // Only stamp block/flex containers, not inline spans
                if (disp !== 'inline' && disp !== 'inline-block') {
                    ancestor.dataset.snapstkImgSrc = resolvedSrc;
                    break;
                }
            }
            ancestor = ancestor.parentElement;
        }
    }

    // ── Stamp landmark dimensions at bottom of scroll ────────────────────────
    // After scroll back to top, React collapses off-screen sections to zero height.
    // Stamp actual dimensions NOW so pagemap captures correct h values.
    for (const el of document.querySelectorAll('main, section')) {
        const r = el.getBoundingClientRect();
        if (r.width > 2) {
            el.dataset.snapstkH = Math.round(r.height + window.scrollY);
        }
    }

    // ── Scroll back to top for clean rect measurement ─────────────────────────
    doScroll(0);
    await new Promise(r => setTimeout(r, 600));
    console.log('[SnapStak] waitForImages: complete, page ready for capture');
}


async function prefetchSVGSprites(rootEl) {
    const spriteCache = new Map(); // spriteURL → parsed Document
    const symbolCache = new Map(); // "spriteURL#id" → inlined SVG string

    const _spriteRoot = rootEl || document;
    const svgEls = Array.from(_spriteRoot.querySelectorAll('svg'));
    const spriteURLs = new Set();

    // ── Also scan same-origin iframes — sprite sheets loaded inside embeds
    // (e.g. live-timing widgets) are invisible to document.querySelectorAll.
    // Cross-origin iframes throw on contentDocument access — catch and skip.
    const _iframeDocuments = [];
    try {
        const _iframes = Array.from(document.querySelectorAll('iframe'));
        for (const _iframe of _iframes) {
            try {
                const _iDoc = _iframe.contentDocument || _iframe.contentWindow?.document;
                if (_iDoc && _iDoc.readyState !== 'uninitialized') _iframeDocuments.push(_iDoc);
            } catch (_) { /* cross-origin — skip */ }
        }
    } catch (_) { }

    const _allSVGEls = [
        ...svgEls,
        ..._iframeDocuments.flatMap(d => Array.from(d.querySelectorAll('svg'))),
    ];

    for (const svgEl of _allSVGEls) {
        const useEl = svgEl.querySelector('use');
        if (!useEl) continue;
        const XLINK_NS = 'http://www.w3.org/1999/xlink';
        const href = useEl.getAttributeNS(XLINK_NS, 'href')
            || useEl.getAttribute('href')
            || useEl.getAttribute('xlink:href')
            || '';
        if (!href.includes('.svg#')) continue;
        const [spriteURL] = href.split('#');
        if (spriteURL) spriteURLs.add(spriteURL);
    }

    // Fetch each unique sprite sheet — instant from browser cache
    const fetchPromises = Array.from(spriteURLs).map(async (spriteURL) => {
        try {
            const abs = new URL(spriteURL, window.location.origin).href;
            const res = await fetch(abs, { cache: 'force-cache' });
            if (!res.ok) return;
            const text = await res.text();
            const parser = new DOMParser();
            const doc = parser.parseFromString(text, 'image/svg+xml');
            spriteCache.set(spriteURL, doc);
            // Also store under absolute URL key
            if (abs !== spriteURL) spriteCache.set(abs, doc);
            console.log('[SnapStak] Sprite from cache:', spriteURL);
        } catch (e) {
            console.warn('[SnapStak] Failed to read sprite:', spriteURL, e.message);
        }
    });

    await Promise.all(fetchPromises);

    // Build lookup: "spriteURL#symbolId" → standalone SVG string
    for (const [spriteURL, doc] of spriteCache.entries()) {
        const symbols = doc.querySelectorAll('symbol');
        for (const sym of symbols) {
            const symId = sym.getAttribute('id');
            if (!symId) continue;
            const vb = sym.getAttribute('viewBox') || '0 0 24 24';
            const inner = sym.innerHTML;
            const _symbolIsLogo = /\bfill\s*=\s*["'](?!none|currentColor)[^"']/i.test(inner);
            const fixedInner = _symbolIsLogo ? inner : inner.replace(
                /<(path|line|polyline|polygon|circle|rect|ellipse)(\s[^>]*)?\/>/gi,
                (match, tagName, attrs) => {
                    const a = attrs || '';
                    if (!/\bfill\s*=/.test(a) && !/\bstroke\s*=/.test(a)) {
                        return `<${tagName}${a} fill="none" stroke="currentColor"/>`;
                    }
                    return match;
                }
            );
            const inlined = _symbolIsLogo
                ? `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${vb}" width="100%" height="100%">${fixedInner}</svg>`
                : `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${vb}" width="100%" height="100%" style="color:inherit;fill:currentColor;">${fixedInner}</svg>`;
            // Store under relative, absolute, and hash-only keys
            symbolCache.set(`${spriteURL}#${symId}`, inlined);
            try {
                const absSpriteURL = new URL(spriteURL, window.location.origin).href;
                if (absSpriteURL !== spriteURL) symbolCache.set(`${absSpriteURL}#${symId}`, inlined);
            } catch (e) { }
        }
        console.log('[SnapStak] Cached', symbols.length, 'symbols from', spriteURL);
    }

    // ── Inline symbols already in page DOM (hash-only href="#id") ─────────────
    const _inlineSVGs = Array.from(document.querySelectorAll('svg'));
    let _inlineCount = 0;
    for (const _svg of _inlineSVGs) {
        const _symbols = Array.from(_svg.querySelectorAll('symbol'));
        for (const _sym of _symbols) {
            const _symId = _sym.getAttribute('id');
            if (!_symId) continue;
            const _vb = _sym.getAttribute('viewBox') || '0 0 24 24';
            const _inner = _sym.innerHTML;
            const _isLogo = /\bfill\s*=\s*["'](?!none|currentColor)[^"']/i.test(_inner);
            const _fixed = _isLogo ? _inner : _inner.replace(
                /<(path|line|polyline|polygon|circle|rect|ellipse)(\s[^>]*)?\/?>/gi,
                (match, tagName, attrs) => {
                    const a = attrs || '';
                    if (!/\bfill\s*=/.test(a) && !/\bstroke\s*=/.test(a)) {
                        return `<${tagName}${a} fill="none" stroke="currentColor"/>`;
                    }
                    return match;
                }
            );
            const _inlined = _isLogo
                ? `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${_vb}" width="100%" height="100%">${_fixed}</svg>`
                : `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${_vb}" width="100%" height="100%" style="color:inherit;fill:currentColor;">${_fixed}</svg>`;
            symbolCache.set(`#${_symId}`, _inlined);
            try {
                symbolCache.set(`${window.location.href.split('#')[0]}#${_symId}`, _inlined);
                symbolCache.set(`${window.location.origin}#${_symId}`, _inlined);
            } catch (e) { }
            _inlineCount++;
        }
    }
    if (_inlineCount > 0) console.log(`[SnapStak] Cached ${_inlineCount} inline symbols from page DOM`);

    console.log('[SnapStak] SVG symbol cache size:', symbolCache.size);
    return symbolCache;
}

// =============================================================================
// STAMP SEGMENT IDs
//
// Walks the DOM and stamps data-segment-id="ss_{8hex}_{unixTs}" onto every
// layout container — an element that:
//   1. Has layout CSS: display flex/grid/block/inline-block/table-cell
//   2. Has explicit dimensions (width > 0, height > 0)
//   3. Directly contains at least one text node, media element, or actionable
//      element (a, button, input, select, textarea)
//
// Also stamps data-responsive="true" on containers that hold a <picture>,
// <source srcset>, or <img srcset> — indicating responsive media.
//
// The same _runTs is shared across all segments in one extraction run.
// Each container gets a unique 8-char hex string.
// =============================================================================
function stampSegmentIds(runTs) {
    const LAYOUT_DISPLAYS = new Set(['flex', 'inline-flex', 'grid', 'inline-grid', 'block', 'inline-block', 'table-cell', 'table-row', 'list-item']);
    const ACTIONABLE_TAGS = new Set(['a', 'button', 'input', 'select', 'textarea']);
    const MEDIA_TAGS = new Set(['img', 'video', 'audio', 'picture', 'canvas', 'svg']);
    const SKIP_TAGS = new Set(['script', 'style', 'noscript', 'head', 'html', 'meta', 'link']);

    // Generate a unique 8-char hex string
    function hex8() {
        return Math.floor(Math.random() * 0xFFFFFFFF).toString(16).padStart(8, '0');
    }

    // Does this element directly contain text, media, or actionable children?
    function isLayoutContainer(el) {
        const cs = window.getComputedStyle(el);
        const display = cs.display || '';

        // Must have a layout display value
        if (!LAYOUT_DISPLAYS.has(display)) return false;

        // Must have real dimensions
        const rect = el.getBoundingClientRect();
        if (rect.width < 2 || rect.height < 2) return false;

        // Must directly contain qualifying content
        // Check direct text nodes
        for (const node of el.childNodes) {
            if (node.nodeType === Node.TEXT_NODE && node.textContent.trim()) return true;
        }
        // Check direct element children for media or actionable tags
        for (const child of el.children) {
            const ct = child.tagName.toLowerCase();
            if (MEDIA_TAGS.has(ct) || ACTIONABLE_TAGS.has(ct)) return true;
        }

        return false;
    }

    // Is this container holding responsive media?
    function isResponsiveMedia(el) {
        // Contains a <picture> element
        if (el.querySelector('picture')) return true;
        // Contains an <img> with srcset
        const imgs = el.querySelectorAll('img[srcset], img[data-srcset]');
        if (imgs.length > 0) return true;
        // Contains a <source> with srcset
        const sources = el.querySelectorAll('source[srcset]');
        if (sources.length > 0) return true;
        return false;
    }

    // Is this element a deliberate 1px (or 2px) visual divider/separator?
    // These are intentional design elements — full-width, height ≤ 2px,
    // with a background color or border. Never dropped on size alone.
    function isDivider(el) {
        const rect = el.getBoundingClientRect();
        if (rect.height > 2 || rect.width < 50) return false;
        const cs = window.getComputedStyle(el);
        const hasBg = cs.backgroundColor && cs.backgroundColor !== 'rgba(0, 0, 0, 0)' && cs.backgroundColor !== 'transparent';
        const hasBorder = cs.borderTopWidth && parseFloat(cs.borderTopWidth) > 0;
        return hasBg || hasBorder;
    }

    const LANDMARK_TAGS = new Set(['header', 'footer', 'nav', 'main', 'section', 'article', 'aside', 'form']);

    // Walk the entire DOM
    const allEls = document.querySelectorAll('*');
    let count = 0;
    for (const el of allEls) {
        const tag = el.tagName.toLowerCase();
        if (SKIP_TAGS.has(tag)) continue;

        // Always stamp app-header and footer divs — they are the chrome zones
        const _testId = el.getAttribute('data-testid') || '';
        const _isChromeZone = _testId === 'app-header' || _testId === 'footer';

        // Stamp segmentId on layout containers, semantic landmarks, chrome zones, OR deliberate dividers
        if (!el.dataset.segmentId && (isLayoutContainer(el) || LANDMARK_TAGS.has(tag) || _isChromeZone || isDivider(el))) {
            const rect = el.getBoundingClientRect();
            if (rect.width >= 2 && (rect.height >= 2 || isDivider(el))) {
                el.dataset.segmentId = `ss_${hex8()}_${runTs}`;
                count++;
            }
        }

        // Stamp responsive independently — any element containing responsive media
        if (!el.dataset.responsive && isResponsiveMedia(el)) {
            el.dataset.responsive = 'true';
        }
    }
    console.log(`[SnapStak] Stamped ${count} segmentIds (ts: ${runTs})`);
}

// =============================================================================
// MOBILE EXTRACTION — called by background.js AFTER CDP sets inner window to 390px
// 1. translateX centres the 390px page visually in the browser window
// 2. waitForImages() runs the full beam scan — user watches the centred 390px page
// 3. removeProperty restores before serializeDOM() — coordinates are clean 390px
// =============================================================================
// ── expandHorizontalCarousels ─────────────────────────────────────────────────
// Finds all horizontal scroll/carousel containers in the DOM, scrolls through
// each item programmatically, and stamps absolute page coordinates onto each
// off-screen child via data-snapstak-carousel-rect. serializeDOM then reads
// these stamps and serializes every item as if it were in the visible viewport.
//
// Why this approach instead of setting overflow:visible (the vertical method):
// Setting overflow:visible on a flex carousel causes items to reflow — they
// collapse to their natural width and stack. The layout is destroyed. Instead,
// we keep the carousel intact, scroll to each item, read its live rect, store
// the absolute coordinates, then restore scroll position to 0. serializeDOM
// picks up the stamps and places items at correct absolute positions in the SVG.
//
// Detects carousels by: scrollWidth > clientWidth + 4 AND overflowX auto/scroll.
// Also detects scroll-snap containers (scroll-snap-type set).
// Capped at 40 items per carousel to avoid infinite loops on mega-carousels.
// All scroll positions restored to 0 after capture.
async function expandHorizontalCarousels() {
    const CAROUSEL_ATTR = 'data-snapstak-carousel-rect';
    const MAX_ITEMS = 40;
    const stamped = [];

    // Find all horizontal scroll containers
    const allEls = Array.from(document.querySelectorAll('*'));
    const carousels = allEls.filter(el => {
        const cs = window.getComputedStyle(el);
        const ox = cs.overflowX;
        if (ox !== 'auto' && ox !== 'scroll') return false;
        if (el.scrollWidth <= el.clientWidth + 4) return false;
        if (!el.children.length) return false;
        return true;
    });

    if (carousels.length === 0) return;
    console.log(`[SnapStak] expandHorizontalCarousels: ${carousels.length} carousel(s) found`);

    for (const carousel of carousels) {
        // Read container position BEFORE any scrolling — scroll position does not
        // affect getBoundingClientRect on the container itself.
        const containerRect = carousel.getBoundingClientRect();
        const containerTop = Math.round(containerRect.top + window.scrollY);
        const containerLeft = Math.round(containerRect.left + window.scrollX);
        const savedScrollLeft = carousel.scrollLeft;

        const items = Array.from(carousel.children);
        if (items.length === 0) continue;

        // Scroll to position 0 first — get a clean measurement of item dimensions
        // while the first item is fully visible.
        carousel.scrollLeft = 0;
        await new Promise(r => requestAnimationFrame(r));

        // Measure the first item to get item width and gap from the live browser.
        // All items in a carousel share the same width (set by CSS min-width/width).
        const firstRect = items[0].getBoundingClientRect();
        const itemH = Math.round(firstRect.height);

        let processed = 0;
        for (let i = 0; i < items.length && processed < MAX_ITEMS; i++) {
            const item = items[i];

            // x position = container's absolute left + item's offsetLeft within container.
            // offsetLeft is the item's distance from the carousel's left edge — independent
            // of scroll position. This gives the true absolute x for the SVG.
            const itemAbsX = containerLeft + item.offsetLeft;
            const itemAbsY = containerTop;

            // For width: scroll this item into view and measure live — captures
            // any item-specific width differences (first/last partial items etc).
            carousel.scrollLeft = item.offsetLeft;
            await new Promise(r => requestAnimationFrame(r));
            const itemRect = item.getBoundingClientRect();
            const itemAbsW = Math.round(itemRect.width);
            const itemAbsH = Math.round(itemRect.height) || itemH;

            if (itemAbsW < 2 || itemAbsH < 2) continue;

            // Stamp item with correct absolute coordinates
            item.setAttribute(CAROUSEL_ATTR, JSON.stringify({
                x: itemAbsX,
                y: itemAbsY,
                w: itemAbsW,
                h: itemAbsH,
                carouselIndex: i,
                carouselLeft: containerLeft,
                carouselTop: containerTop,
            }));
            stamped.push(item);

            // Stamp all descendants using their live rects (item is in view).
            // Their screen position is accurate now — offset by containerLeft - containerLeft
            // cancels, so we only need to add containerLeft to left + scrollX.
            for (const child of Array.from(item.querySelectorAll('*'))) {
                const cr = child.getBoundingClientRect();
                if (cr.width < 1 && cr.height < 1) continue;
                // child's absolute x = container left + (child screen left - container screen left) + item.offsetLeft
                const childAbsX = containerLeft + item.offsetLeft + Math.round(cr.left - containerRect.left - item.offsetLeft + carousel.scrollLeft);
                const childAbsY = Math.round(cr.top + window.scrollY);
                child.setAttribute(CAROUSEL_ATTR, JSON.stringify({
                    x: childAbsX,
                    y: childAbsY,
                    w: Math.round(cr.width),
                    h: Math.round(cr.height),
                    carouselIndex: i,
                }));
                stamped.push(child);
            }

            processed++;
        }

        // Restore scroll position
        carousel.scrollLeft = savedScrollLeft;
        await new Promise(r => requestAnimationFrame(r));

        console.log(`[SnapStak] expandHorizontalCarousels: stamped ${processed} items | container x=${containerLeft} y=${containerTop}`);
    }

    console.log(`[SnapStak] expandHorizontalCarousels: ${stamped.length} elements stamped total`);
    return stamped;
}


// =============================================================================
// DISCOVER HIDDEN COMPONENTS
//
// Generic rule — no pattern matching, no framework assumptions:
//
//   A hidden component is any element that:
//     1. Is hidden — display:none, visibility:hidden/collapse, or [hidden] attr
//     2. Has at least one child element
//
// Outermost-only: if a hidden ancestor is captured, all its descendants are
// skipped — we never double-capture nested hidden trees.
//
// The "hidden" check uses getComputedStyle so inherited display:none (where
// a visible parent hides children transitively) is NOT treated as hidden —
// only the element that is ITSELF the source of hiding is captured. This is
// the correct DOM truth: the outermost hidden root is the component boundary.
//
// For each root:
//   1. Force-show it and its ancestor chain (display:block, visibility:hidden
//      so it renders layout without becoming visible to the user)
//   2. serializeDOM scoped to that root — captures full subtree
//   3. Re-root coordinates to (0,0) of the component
//   4. Restore all original styles exactly
// =============================================================================
async function discoverHiddenComponents(svgSymbolCache) {
    svgSymbolCache = svgSymbolCache || new Map();

    const SKIP_TAGS = new Set(['script', 'noscript', 'style', 'head', 'meta', 'link', 'title', 'base']);

    // Returns true if this element is ITSELF the source of hiding —
    // i.e. its own inline style or stylesheet rule makes it hidden,
    // independent of what its parent computes.
    // We check the element's own computed value against its parent's computed
    // value: if the parent is also display:none, the parent is the source, not
    // this element — it would be captured as part of the parent's subtree.
    function isOwnHiddenRoot(el) {
        const cs = window.getComputedStyle(el);
        const isDisplayNone  = cs.display === 'none';
        const isVisHidden    = cs.visibility === 'hidden' || cs.visibility === 'collapse';
        const hasHiddenAttr  = el.hasAttribute('hidden');

        if (!isDisplayNone && !isVisHidden && !hasHiddenAttr) return false;

        // Must have at least one child element — leaf nodes are not components
        if (el.children.length === 0) return false;

        // Confirm this element is the SOURCE of hiding (not inheriting from parent).
        // If the parent is already display:none, the parent is the root — skip this child.
        const parent = el.parentElement;
        if (parent && parent !== document.body) {
            const pcs = window.getComputedStyle(parent);
            // Parent is also hidden — this element is a descendant of another hidden root,
            // it will be captured as part of that root's serializeDOM subtree.
            if (pcs.display === 'none') return false;
            // Parent visibility:hidden — same logic, parent is the source
            if ((isVisHidden || hasHiddenAttr) &&
                (pcs.visibility === 'hidden' || pcs.visibility === 'collapse')) return false;
        }

        return true;
    }

    // ── Walk DOM — collect outermost hidden roots only ────────────────────────
    const hiddenRoots = [];

    function walk(el) {
        if (!el || el.nodeType !== 1) return;
        if (SKIP_TAGS.has((el.tagName || '').toLowerCase())) return;

        if (isOwnHiddenRoot(el)) {
            hiddenRoots.push(el);
            return; // never recurse into a captured root
        }

        for (let i = 0; i < el.children.length; i++) {
            walk(el.children[i]);
        }
    }

    walk(document.body);

    if (hiddenRoots.length === 0) {
        console.log('[SnapStak] discoverHiddenComponents: no hidden components found');
        return [];
    }
    console.log(`[SnapStak] discoverHiddenComponents: found ${hiddenRoots.length} hidden root(s)`);

    // ── Serialise each root ───────────────────────────────────────────────────
    const components = [];

    for (const root of hiddenRoots) {
        const tag = (root.tagName || '').toLowerCase();

        // Best available label — prefer explicit accessibility/test attributes,
        // fall back to id, then tag name. No pattern guessing.
        const label = root.getAttribute('aria-label')
            || root.getAttribute('data-testid')
            || root.getAttribute('resource-id')
            || root.getAttribute('accessibilityidentifier')
            || root.getAttribute('data-component')
            || root.id
            || root.getAttribute('role')
            || tag;

        // Component type — read directly from ARIA role if present, otherwise
        // use tag name. No class/id pattern matching.
        const role = (root.getAttribute('role') || '').toLowerCase();
        const componentType = role || tag;

        // ── Force-show: walk UP the ancestor chain first ──────────────────────
        // We must un-hide every ancestor that is display:none before the root
        // itself can be measured. Walk from root up to body, collect saved
        // styles, then apply overrides from body downward so the cascade is
        // correct when we read layout.
        const savedStyles = [];

        const ancestorChain = [];
        let node = root;
        while (node && node !== document.body) {
            ancestorChain.push(node);
            node = node.parentElement;
        }
        // Apply from outermost inward so each element's computed style is valid
        // when the next one is processed.
        for (let i = ancestorChain.length - 1; i >= 0; i--) {
            const n = ancestorChain[i];
            const cs = window.getComputedStyle(n);
            const saved = {
                el:         n,
                display:    n.style.display,
                visibility: n.style.visibility,
                height:     n.style.height,
                maxHeight:  n.style.maxHeight,
                overflow:   n.style.overflow,
            };
            let changed = false;
            if (cs.display === 'none') {
                n.style.setProperty('display', 'block', 'important');
                // Keep invisible — renders layout geometry without showing to user
                n.style.setProperty('visibility', 'hidden', 'important');
                changed = true;
            }
            if (cs.visibility === 'hidden' || cs.visibility === 'collapse') {
                n.style.setProperty('visibility', 'hidden', 'important');
                changed = true;
            }
            // height:0 + overflow:hidden collapses the element to zero —
            // force open so serializeDOM gets real dimensions.
            const _h  = parseFloat(cs.height);
            const _mh = parseFloat(cs.maxHeight);
            if ((_h === 0 || _mh === 0) &&
                (cs.overflow === 'hidden' || cs.overflowY === 'hidden')) {
                n.style.setProperty('height', 'auto', 'important');
                n.style.setProperty('max-height', 'none', 'important');
                n.style.setProperty('overflow', 'visible', 'important');
                changed = true;
            }
            if (changed) savedStyles.push(saved);
        }

        void root.offsetHeight; // force layout reflow

        // ── Stamp segmentId if not already present ────────────────────────────
        if (!root.dataset.segmentId) {
            root.dataset.segmentId = 'ss_hc_'
                + Math.floor(Math.random() * 0xFFFFFFFF).toString(16)
                + '_' + Math.floor(Date.now() / 1000);
        }

        // ── Measure root rect BEFORE serializeDOM (same pattern as _zoneSnapshot) ──
        const rootRect = root.getBoundingClientRect();
        const originX  = Math.round(rootRect.left + (window.scrollX || 0));
        const originY  = Math.round(rootRect.top  + (window.scrollY || 0));
        const rootW    = Math.round(rootRect.width)  || root.scrollWidth  || window.innerWidth;
        const rootH    = Math.round(rootRect.height) || root.scrollHeight || window.innerHeight;

        // ── Serialise subtree ─────────────────────────────────────────────────
        // Collect BOTH visible and invisible elements.
        // The force-show logic sets visibility:hidden on ancestors so the DOM
        // renders layout geometry without showing content to the user. This means
        // serializeDOM marks children as hidden (isHidden=true) and puts them in
        // invisible[] — but they DO have real rects because the browser laid them
        // out. We need both arrays to get the full element tree.
        const { visible: _visEls, invisible: _invEls } = serializeDOM(svgSymbolCache, root);

        // Merge: visible first, then invisible elements that have real geometry.
        // Invisible-with-geometry are the dialog children hidden by visibility:hidden.
        // Mark them as hidden=false so the SVG serialiser renders them — they are
        // the actual component content, just temporarily hidden from the live page.
        const _invWithGeom = _invEls.filter(e =>
            e.rect && e.rect.width > 0 && e.rect.height > 0
        );
        for (const e of _invWithGeom) e.hidden = false;
        const elements = [..._visEls, ..._invWithGeom];

        // Re-root coordinates to component origin (0,0)
        for (const e of elements) {
            if (e.rect) {
                e.rect.x -= originX;
                e.rect.y -= originY;
            }
            if (e.parentRect) {
                e.parentRect.x -= originX;
                e.parentRect.y -= originY;
            }
        }

        // ── Capture CSS + JS scoped to this hidden component root ─────────────
        // Both extractComponentCSS and the inline JS scan are available here —
        // discoverHiddenComponents runs inside extractMobile() where they are
        // in scope. Called while the component is still force-shown.
        let _hcCssB64 = '';
        try {
            const _hcCss = extractComponentCSS(root);
            if (_hcCss.matched.length > 0 || _hcCss.media.length > 0) {
                const _json = JSON.stringify(_hcCss);
                const _bytes = new TextEncoder().encode(_json);
                let _bin = '';
                for (const b of _bytes) _bin += String.fromCharCode(b);
                _hcCssB64 = btoa(_bin);
            }
        } catch (_) { }

        let _hcJsB64 = '';
        try {
            // Capture inline on* handlers within this hidden component's subtree
            const _HC_EVENT_ATTRS = [
                'onclick', 'ondblclick', 'onchange', 'oninput', 'onsubmit',
                'onkeydown', 'onkeyup', 'onfocus', 'onblur',
            ];
            const _hcHandlers = [];
            for (const el of [root, ...root.querySelectorAll('*')]) {
                for (const attr of _HC_EVENT_ATTRS) {
                    const val = el.getAttribute(attr);
                    if (val && val.trim()) {
                        _hcHandlers.push({
                            tag:       el.tagName.toLowerCase(),
                            id:        el.id || '',
                            className: (typeof el.className === 'string' ? el.className : '').slice(0, 80),
                            role:      el.getAttribute('role') || '',
                            handler:   attr,
                            value:     val.slice(0, 300),
                        });
                    }
                }
            }
            if (_hcHandlers.length > 0) {
                const _hcJs = { scripts: [], inlineHandlers: _hcHandlers };
                const _json = JSON.stringify(_hcJs);
                const _bytes = new TextEncoder().encode(_json);
                let _bin = '';
                for (const b of _bytes) _bin += String.fromCharCode(b);
                _hcJsB64 = btoa(_bin);
            }
        } catch (_) { }

        // ── Restore all saved styles (reverse order — innermost first) ────────
        for (let i = savedStyles.length - 1; i >= 0; i--) {
            const s = savedStyles[i];
            s.el.style.removeProperty('display');
            s.el.style.removeProperty('visibility');
            s.el.style.removeProperty('height');
            s.el.style.removeProperty('max-height');
            s.el.style.removeProperty('overflow');
            if (s.display)    s.el.style.display    = s.display;
            if (s.visibility) s.el.style.visibility = s.visibility;
            if (s.height)     s.el.style.height      = s.height;
            if (s.maxHeight)  s.el.style.maxHeight   = s.maxHeight;
            if (s.overflow)   s.el.style.overflow    = s.overflow;
        }

        // Drop elements with no content and no geometry
        const filtered = elements.filter(e =>
            e.textContent || e.src || e.svgDataURI || e.bgImage ||
            (e.rect && e.rect.width > 0 && e.rect.height > 0)
        );

        if (filtered.length === 0) continue;

        console.log(`[SnapStak] Hidden component: "${label}" (${componentType}) — ${filtered.length} elements | ${rootW}x${rootH}px`);

        components.push({
            componentId:   'hc_' + components.length,
            componentType,
            label:         String(label).slice(0, 80),
            tag,
            width:         rootW,
            height:        rootH,
            segmentId:     root.dataset.segmentId,
            elements:      filtered,
            cssB64:        _hcCssB64,
            jsB64:         _hcJsB64,
        });
    }

    console.log(`[SnapStak] discoverHiddenComponents: ${components.length} component(s) serialised`);
    return components;
}

async function extractMobile(mode) {
    console.log('[SnapStak] Extracting mobile DOM snapshot at 390px...');

    const svgSymbolCache = await prefetchSVGSprites();

    // Scroll the page to trigger lazy loading, then settle
    await waitForImages();

    // Stamp segment IDs AFTER scroll so all dynamically loaded elements get IDs
    // (stampSegmentIds is also called from the C# runner but that runs before scroll)
    stampSegmentIds(Math.floor(Date.now() / 1000));

    // ── Expand horizontal carousels — stamp off-screen item coords ────────────
    // Must run after visual centering is removed (clean coords) and before
    // serializeDOM so every carousel item gets an absolute-position stamp.
    await expandHorizontalCarousels();

    const { visible, invisible } = (mode === 'hidden') ? { visible: [], invisible: [] } : serializeDOM(svgSymbolCache);

    // ── ConteX Law — Structure Pillar: root background colour ────────────────
    // serializeDOM captures each element's own computed backgroundColor.
    // The page root (body/html) holds the true surface colour — the header itself
    // is transparent and inherits it. Without this walk the mobile root rect is
    // always transparent, forcing the AI to guess the background.
    // Mirrors the identical fix in extractElement() — browser is source of truth.
    if (visible.length > 0) {
        if (!visible[0].cssProps) visible[0].cssProps = {};
        if (!visible[0].cssProps.backgroundColor ||
            visible[0].cssProps.backgroundColor === 'rgba(0, 0, 0, 0)' ||
            visible[0].cssProps.backgroundColor === 'transparent') {
            let _bgNode = document.body;
            while (_bgNode && _bgNode !== document.documentElement) {
                const _bg = window.getComputedStyle(_bgNode).backgroundColor;
                if (_bg && _bg !== 'rgba(0, 0, 0, 0)' && _bg !== 'transparent') {
                    visible[0].cssProps.backgroundColor = _bg;
                    console.log('[SnapStak] Mobile root background resolved from ancestor:', _bg);
                    break;
                }
                _bgNode = _bgNode.parentElement;
            }
        }
    }

    // ── ConteX Law — Behaviour Pillar — per-zone CSS extraction ─────────────
    // extractComponentCSS is scoped to a specific root element so only the CSS
    // rules that match elements WITHIN that zone are captured.
    // Called once per zone (header / main / navbar) AFTER zone detection so each
    // landmark receives its own focused CSS — never the whole-page stylesheet.
    // Also called per pageMap entry to produce per-section CSS (section, article,
    // aside, form, and div-based chrome zones).
    function extractComponentCSS(rootEl) {
        const elements = [rootEl, ...rootEl.querySelectorAll('*')];
        const elementSet = new Set(elements);
        const matchedRules = [];
        const behaviorRules = [];
        const mediaRules = [];
        const keyframes = [];
        const usedAnimations = new Set();
        for (const el of elements) {
            const cs = window.getComputedStyle(el);
            const anim = cs.animationName;
            if (anim && anim !== 'none') anim.split(',').forEach(a => usedAnimations.add(a.trim()));
        }
        function stripPseudos(sel) {
            return sel.replace(/:{1,2}(hover|focus|focus-within|focus-visible|active|visited|checked|disabled|enabled|placeholder-shown|placeholder|before|after|selection|marker|backdrop|first-child|last-child|first-of-type|last-of-type|only-child|only-of-type|empty|root|target|not\([^)]*\)|nth-child\([^)]*\)|nth-of-type\([^)]*\)|is\([^)]*\)|where\([^)]*\)|has\([^)]*\))/gi, '').replace(/\.\S+:\S+/g, '').trim();
        }
        function selectorMatchesComponent(sel) {
            const base = stripPseudos(sel);
            if (!base) return true;
            try { return Array.from(document.querySelectorAll(base)).some(el => elementSet.has(el)); } catch (_) { return false; }
        }
        function isBehaviorSelector(sel) {
            return /:(hover|focus|focus-within|focus-visible|active|disabled|checked)/i.test(sel);
        }
        try {
            for (const sheet of Array.from(document.styleSheets)) {
                let rules;
                try { rules = sheet.cssRules || sheet.rules; } catch (_) { continue; }
                if (!rules) continue;
                // ── ConteX Law V5: resolve CSS custom properties at capture time ──
                // rule.style.cssText contains raw var(--token) references that the AI
                // cannot resolve. getPropertyValue() asks the live browser for the exact
                // computed value — the same mechanism used by the desktop CSS capture.
                // Both the token name AND the resolved value are stored so the AI
                // receives exact px/font/color values, never guesses.
                const _elStyle = window.getComputedStyle(rootEl);
                const _docStyle = window.getComputedStyle(document.documentElement);
                function _resolveVars(cssText) {
                    return (cssText || '').replace(/var\(\s*(--[a-zA-Z0-9_-]+)\s*(?:,[^)]+)?\)/g, (match, prop) => {
                        const val = _elStyle.getPropertyValue(prop).trim() || _docStyle.getPropertyValue(prop).trim();
                        return val ? val : match; // preserve token if browser cannot resolve
                    });
                }
                for (const rule of Array.from(rules)) {
                    if (rule.type === CSSRule.STYLE_RULE) {
                        const sel = rule.selectorText || '';
                        if (selectorMatchesComponent(sel)) {
                            const entry = { selector: sel, properties: _resolveVars(rule.style.cssText) };
                            if (isBehaviorSelector(sel)) behaviorRules.push(entry);
                            else matchedRules.push(entry);
                        }
                    } else if (rule.type === CSSRule.MEDIA_RULE) {
                        const mediaText = rule.conditionText || rule.media?.mediaText || '';
                        const matchedInMedia = [];
                        for (const mr of Array.from(rule.cssRules || [])) {
                            if (mr.type === CSSRule.STYLE_RULE) {
                                const sel = mr.selectorText || '';
                                if (selectorMatchesComponent(sel)) matchedInMedia.push({ selector: sel, properties: _resolveVars(mr.style.cssText) });
                            }
                        }
                        if (matchedInMedia.length) mediaRules.push({ media: mediaText, rules: matchedInMedia });
                    } else if (rule.type === CSSRule.KEYFRAMES_RULE) {
                        if (usedAnimations.has(rule.name)) keyframes.push({ name: rule.name, cssText: rule.cssText });
                    }
                }
            }
        } catch (_) { }
        return { matched: matchedRules, behavior: behaviorRules, media: mediaRules, keyframes };
    }

    // ── ConteX Law — Behaviour Pillar: JS extraction ────────────────────────
    // Captures ONLY JavaScript that directly impacts the HTML component behaviour.
    //
    // External bundles (React runtime, vendor chunks, analytics loaders) are
    // never captured — they are megabyte compiled artifacts that the AI cannot
    // use and would only add noise to the payload.
    //
    // What IS captured:
    //   inlineHandlers[] — on* event attributes directly on DOM elements
    //     (onclick, onchange, onsubmit, etc.). These are the most direct signal
    //     of JS-driven behaviour — they are authored, not compiled.
    //     Each entry: { tag, id, className, role, handler, value }
    //
    //   scripts[] — ONLY small inline <script> blocks that configure app
    //     behaviour (e.g. window.__APP_CONFIG__, feature flags, init data).
    //     Threshold: < 500 chars after trimming. Anything larger is a compiled
    //     bundle fragment and is not useful to the Behaviour AI.
    //     SnapStak engine and MAUI stubs are always excluded.
    //     Each entry: { type: 'inline', content }
    //
    // ARIA state attributes (aria-expanded, aria-modal, aria-controls etc.)
    // and data-* behaviour attributes are already captured per-element by
    // serializeDOM — they do not need to be re-captured here.

    let componentJS = null;
    try {
        // ── Inline event handlers on DOM elements ─────────────────────────────
        const _EVENT_ATTRS = [
            'onclick', 'ondblclick', 'onchange', 'oninput', 'onsubmit',
            'onkeydown', 'onkeyup', 'onkeypress',
            'onmouseenter', 'onmouseleave', 'onmouseover',
            'onfocus', 'onblur', 'onscroll',
        ];
        const _handlers = [];
        for (const el of Array.from(document.querySelectorAll('*'))) {
            for (const attr of _EVENT_ATTRS) {
                const val = el.getAttribute(attr);
                if (val && val.trim()) {
                    _handlers.push({
                        tag:       el.tagName.toLowerCase(),
                        id:        el.id || '',
                        className: (typeof el.className === 'string' ? el.className : '').slice(0, 80),
                        role:      el.getAttribute('role') || '',
                        handler:   attr,
                        value:     val.slice(0, 300),
                    });
                }
            }
        }

        // ── Small inline <script> config blocks only ──────────────────────────
        const _EXCLUDE_MARKERS = [
            '__snapstak_mobile_loaded__',   // SnapStak engine
            'window.__snapstak',
            'window.Capacitor',             // MAUI Capacitor stub
            'window.cordova',               // MAUI Cordova stub
            'window.PhoneGap',
        ];
        const _INLINE_SIZE_LIMIT = 500; // chars — anything larger is a compiled bundle

        const _scripts = [];
        for (const s of Array.from(document.querySelectorAll('script'))) {
            // External scripts (src=) are never useful to Behaviour AI — skip all
            if (s.getAttribute('src')) continue;
            const _content = (s.textContent || '').trim();
            if (!_content) continue;
            // Skip SnapStak engine and MAUI stubs
            if (_EXCLUDE_MARKERS.some(m => _content.includes(m))) continue;
            // Skip anything large — it's a compiled bundle, not config
            if (_content.length > _INLINE_SIZE_LIMIT) continue;
            _scripts.push({ type: 'inline', content: _content });
        }

        if (_handlers.length > 0 || _scripts.length > 0) {
            componentJS = { scripts: _scripts, inlineHandlers: _handlers };
            console.log(`[SnapStak] JS captured: ${_scripts.length} inline config script(s), ${_handlers.length} on* handler(s)`);
        } else {
            console.log('[SnapStak] JS: no DOM-impacting JS found (expected for React/compiled apps)');
        }
    } catch (_jsErr) {
        console.warn('[SnapStak] JS capture failed (non-fatal):', _jsErr.message);
    }

    // ── ConteX Law — Structure Pillar: page dimensions + landmark map ────────
    // Mobile was missing pageWidth, pageHeight, pageMap — causing:
    //   1. SVG height hardcoded to 900 — content clipped at 900px.
    //   2. snapstak:pagemap absent — segment boundaries lost.
    // Use dimensions captured at the bottom of the page (inside waitForImages)
    // BEFORE React collapsed the virtual DOM back to viewport height.
    const mobilePageWidth  = window.__snapstak_full_page_width__
                          || Math.max(document.body.scrollWidth, document.documentElement.scrollWidth);
    const mobilePageHeight = window.__snapstak_full_page_height__
                          || Math.max(document.body.scrollHeight, document.documentElement.scrollHeight);

    const MOBILE_LANDMARK_TAGS = new Set(['header', 'footer', 'nav', 'main', 'section', 'article', 'aside', 'form']);
    const MOBILE_CHROME_TESTIDS = new Set(['app-header', 'footer']);
    const mobilePageMap = [];
    try {
        const _mobileLandmarks = Array.from(document.querySelectorAll('*')).filter(el => {
            const tag = (el.tagName || '').toLowerCase();
            const testId = el.getAttribute('data-testid') || '';
            const isChrome = MOBILE_CHROME_TESTIDS.has(testId);
            if (!MOBILE_LANDMARK_TAGS.has(tag) && !isChrome) return false;
            if (!el.dataset.segmentId) return false;
            const rect = el.getBoundingClientRect();
            if (rect.width < 2 || rect.height < 2) return false;
            // Chrome elements always included
            if (isChrome) return true;
            // Walk up to find the nearest landmark ancestor with a segmentId.
            // If that ancestor is <main>, allow this element through — sections
            // inside the main zone are the content landmarks we want in the pagemap.
            let ancestor = el.parentElement;
            while (ancestor && ancestor !== document.body) {
                const aTag = (ancestor.tagName || '').toLowerCase();
                if (MOBILE_LANDMARK_TAGS.has(aTag) && ancestor.dataset.segmentId) {
                    return aTag === 'main';
                }
                ancestor = ancestor.parentElement;
            }
            return true;
        });
        for (const el of _mobileLandmarks) {
            const tag = (el.tagName || '').toLowerCase();
            const sid = el.dataset.segmentId;
            const rect = el.getBoundingClientRect();
            const label = el.id || el.getAttribute('aria-label') || el.className.split(' ')[0] || tag;
            // Use stamped height for sections/main — rect.height is collapsed after scroll back to top
            const stampedH = el.dataset.snapstkH ? parseInt(el.dataset.snapstkH) : null;
            mobilePageMap.push({
                segmentId: sid, tag,
                label: label.slice(0, 80),
                x: Math.round(rect.left + window.scrollX),
                y: Math.round(rect.top + window.scrollY),
                w: Math.round(rect.width),
                h: stampedH || Math.round(rect.height),
                // ── Per-landmark CSS ──────────────────────────────────────────
                // extractComponentCSS is scoped to this element so only CSS rules
                // matching elements within this landmark are captured.
                // Covers: section, article, aside, form, and all div-based zones
                // (data-testid, role="navigation", etc.).
                // The server reads cssB64 in ExtractMauiZoneSegmentsAsync and
                // prefers it over the parent zone CSS for each segment directory.
                cssB64: (() => {
                    try {
                        const _css = extractComponentCSS(el);
                        const _json = JSON.stringify(_css);
                        const _bytes = new TextEncoder().encode(_json);
                        let _bin = '';
                        for (const b of _bytes) _bin += String.fromCharCode(b);
                        return btoa(_bin);
                    } catch (_) { return ''; }
                })(),
                // Per-landmark JS — inline on* handlers within this landmark's subtree.
                // Compiled bundles are never captured (same rationale as componentJS).
                jsB64: (() => {
                    try {
                        const _JS_ATTRS = [
                            'onclick', 'ondblclick', 'onchange', 'oninput', 'onsubmit',
                            'onkeydown', 'onkeyup', 'onfocus', 'onblur',
                        ];
                        const _lmHandlers = [];
                        for (const _le of [el, ...el.querySelectorAll('*')]) {
                            for (const _la of _JS_ATTRS) {
                                const _lv = _le.getAttribute(_la);
                                if (_lv && _lv.trim()) {
                                    _lmHandlers.push({
                                        tag:     _le.tagName.toLowerCase(),
                                        id:      _le.id || '',
                                        handler: _la,
                                        value:   _lv.slice(0, 300),
                                    });
                                }
                            }
                        }
                        if (_lmHandlers.length === 0) return '';
                        const _lmJs = { scripts: [], inlineHandlers: _lmHandlers };
                        const _lBytes = new TextEncoder().encode(JSON.stringify(_lmJs));
                        let _lBin = '';
                        for (const b of _lBytes) _lBin += String.fromCharCode(b);
                        return btoa(_lBin);
                    } catch (_) { return ''; }
                })(),
                htmlB64: (() => {
                    try {
                        const _bytes = new TextEncoder().encode(el.outerHTML || '');
                        let _bin = '';
                        for (const b of _bytes) _bin += String.fromCharCode(b);
                        return btoa(_bin);
                    } catch (_) { return ''; }
                })(),
            });
        }
        console.log(`[SnapStak] Mobile page map: ${mobilePageMap.length} landmarks | ${mobilePageWidth}x${mobilePageHeight}px`);
    } catch (_mapErr) {
        console.warn('[SnapStak] Mobile page map failed (non-fatal):', _mapErr.message);
    }

    // ── Discover hidden components (modals, drawers, dialogs) ───────────────
    let hiddenComponents = [];
    try {
        hiddenComponents = await discoverHiddenComponents(svgSymbolCache);
    } catch (_hcErr) {
        console.warn('[SnapStak] Hidden component discovery failed (non-fatal):', _hcErr.message);
    }

    // ── Detect WebView app zones directly from DOM ───────────────────────────
    // content.js has full DOM access — we detect header/main/navbar HERE,
    // serialize each as its own domSnapshot, and return them separately.
    // The server receives clean focused components — no guessing required.
    //
    // Detection: walk the structural root (first node with >= 2 meaningful children)
    // Header  = first direct child, h <= 80px, near y=0
    // Navbar  = direct child with h <= 80px near viewport bottom
    // Main    = everything else (the scrollable content)
    //
    // Tracking: window.__snapstak_chrome_sent__ flags header+navbar as already sent
    // on subsequent page navigations so we don't re-send static chrome.

    const _vh = window.innerHeight || 667;
    const _maxChrome = 80;

    // Find the structural root element (first with >= 2 meaningful direct children).
    // A "meaningful" child must have real dimensions OR contain children with real dimensions —
    // this skips React portal roots and empty wrapper divs that have 0x0 rects themselves
    // but contain rendered content (e.g. Toastify containers).
    function _findStructRoot(el) {
        if (!el) return null;
        const kids = Array.from(el.children).filter(c => {
            const r = c.getBoundingClientRect();
            if (r.width > 0 || r.height > 0) return true;
            // Also count children that contain visible descendants
            return c.querySelector && !!c.querySelector('*');
        });
        if (kids.length >= 2) return el;
        for (const child of el.children) {
            const found = _findStructRoot(child);
            if (found) return found;
        }
        return null;
    }

    // Given an element (which may be a tall wrapper), find the actual chrome bar inside it.
    // Looks for the first descendant that: has real dimensions AND height <= maxChrome.
    // Used when the outer container is a wrapper div taller than 80px (e.g. BK footer div
    // that wraps Toastify + the actual nav bar).
    function _findChromeBar(container, maxH) {
        if (!container) return null;
        const r = container.getBoundingClientRect();
        // Container itself qualifies — return it directly
        if (r.height > 0 && r.height <= maxH) return container;
        // Otherwise walk direct children first, then one level deeper
        for (const child of Array.from(container.children)) {
            const cr = child.getBoundingClientRect();
            if (cr.height > 0 && cr.height <= maxH && cr.width > 10) return child;
        }
        for (const child of Array.from(container.children)) {
            for (const grand of Array.from(child.children)) {
                const gr = grand.getBoundingClientRect();
                if (gr.height > 0 && gr.height <= maxH && gr.width > 10) return grand;
            }
        }
        return null;
    }

    const _structRoot = _findStructRoot(document.body);
    let _headerEl = null;
    let _navbarEl  = null;
    let _mainEl    = null;

    if (_structRoot) {
        const _kids = Array.from(_structRoot.children);

        // Pass 1: semantic HTML tags — exact matches only
        for (const c of _kids) {
            const t = c.tagName.toLowerCase();
            if (t === 'header') { _headerEl = c; continue; }
            if (t === 'nav')    { _navbarEl  = c; continue; }
            if (t === 'main')   { _mainEl    = c; continue; }
        }

        // Pass 2: positional heuristic on the direct child itself
        // Works when the chrome bar IS the direct child (not wrapped in a container)
        for (const c of _kids) {
            if (c === _mainEl || c === _headerEl || c === _navbarEl) continue;
            const r = c.getBoundingClientRect();
            if (r.height <= 0) continue;
            const absY = r.top + window.scrollY;
            if (!_headerEl && absY <= 10 && r.height <= _maxChrome) { _headerEl = c; continue; }
            if (!_navbarEl  && absY >= _vh - _maxChrome - 10 && r.height <= _maxChrome) { _navbarEl  = c; continue; }
        }

        // Pass 3: attribute-based detection for React apps that use div wrappers
        // instead of semantic tags. Matches data-testid, resource-id, accessibilityidentifier,
        // and data-mediaquery — patterns used by BK, McDonald's, and similar RN-style apps.
        // Also handles the case where the outer wrapper is a tall container (e.g. the BK
        // "footer" div that wraps both a Toastify portal and the actual bottom nav bar).
        const _headerPatterns = /header/i;
        const _navPatterns    = /footer|navbar|nav-bar|bottom.?nav|tab.?bar/i;

        if (!_headerEl || !_navbarEl) {
            for (const c of _kids) {
                if (c === _mainEl) continue;
                const _tid  = c.getAttribute('data-testid') || '';
                const _rid  = c.getAttribute('resource-id') || '';
                const _aid  = c.getAttribute('accessibilityidentifier') || '';
                const _mq   = c.getAttribute('data-mediaquery') || '';
                const _combined = `${_tid} ${_rid} ${_aid} ${_mq}`;

                if (!_headerEl && _headerPatterns.test(_combined)) {
                    // Find the actual chrome bar inside this container
                    _headerEl = _findChromeBar(c, _maxChrome) || c;
                    continue;
                }
                if (!_navbarEl && _navPatterns.test(_combined)) {
                    // Find the actual chrome bar inside this container
                    _navbarEl = _findChromeBar(c, _maxChrome) || c;
                    continue;
                }
            }
        }

        // Pass 4: largest remaining child = main
        if (!_mainEl) {
            let _bestArea = 0;
            for (const c of _kids) {
                if (c === _headerEl || c === _navbarEl) continue;
                // Also skip elements that contain our detected header/navbar
                // (avoids selecting the outer wrapper as main when header/navbar
                //  were found as descendants of a sibling container)
                if (_headerEl && c.contains(_headerEl)) continue;
                if (_navbarEl && c.contains(_navbarEl)) continue;
                const r = c.getBoundingClientRect();
                const area = r.width * r.height;
                if (area > _bestArea) { _bestArea = area; _mainEl = c; }
            }
        }
    }

    console.log('[SnapStak] Zones detected —',
        'Header:', _headerEl ? (_headerEl.getAttribute('data-testid') || _headerEl.tagName) : 'none',
        '| Main:', _mainEl   ? (_mainEl.getAttribute('data-testid')   || _mainEl.tagName)   : 'none',
        '| Navbar:', _navbarEl  ? (_navbarEl.getAttribute('data-testid')  || _navbarEl.tagName)  : 'none'
    );

    // ── Serialize each zone using serializeDOM with a root element ────────────
    // Re-roots coordinates: subtracts the zone root's absolute origin so every
    // element in the snapshot has coordinates relative to (0,0) of that zone.
    // This means header SVG starts at y=0, main SVG starts at y=0, navbar at y=0.
    //
    // CRITICAL: capture zoneRect BEFORE serializeDOM runs — serializeDOM may
    // trigger layout reflow on some browsers which can shift rects.
    // Also re-root parentRect so text-layout width calculations stay valid
    // within the zone coordinate space.
    function _zoneSnapshot(rootEl, pageW, pageH, pageMapEntries) {
        if (!rootEl) return null;
        // Capture origin BEFORE serializing
        const zoneRect = rootEl.getBoundingClientRect();
        const originX  = Math.round(zoneRect.left + (window.scrollX || 0));
        const originY  = Math.round(zoneRect.top  + (window.scrollY || 0));
        const { visible: els } = serializeDOM(svgSymbolCache, rootEl);
        if (els.length === 0) return null;
        const zoneW = pageW || Math.round(zoneRect.width)  || mobilePageWidth;
        const zoneH = pageH || Math.round(zoneRect.height) || 1;
        // Re-root: subtract zone origin from every element's rect AND parentRect
        for (const el of els) {
            if (el.rect) {
                el.rect.x -= originX;
                el.rect.y -= originY;
            }
            if (el.parentRect) {
                el.parentRect.x -= originX;
                el.parentRect.y -= originY;
            }
        }
        // Override root element height with full zoneH — after scrolling back to top,
        // getBoundingClientRect().height on <main> returns viewport height, not full
        // scrollable height. Without this the SVG data-h is clipped to viewport height
        // and all off-screen content is cut off.
        if (els.length > 0 && els[0].rect) {
            els[0].rect.width  = zoneW;
            els[0].rect.height = zoneH;
        }
        console.log(`[SnapStak] Zone snapshot: origin=(${originX},${originY}) size=${zoneW}x${zoneH} elements=${els.length}`);
        return {
            elements:   els,
            pageWidth:  zoneW,
            pageHeight: zoneH,
            pageMap:    pageMapEntries || [],
        };
    }

    const _headerSnap = _zoneSnapshot(_headerEl, mobilePageWidth, null, []);
    const _navbarSnap  = _zoneSnapshot(_navbarEl,  mobilePageWidth, null, []);

    // Main: pageHeight = full scrollable content height (captured during scroll),
    // minus header height, so the SVG height matches actual content only.
    const _headerH = _headerEl ? Math.round(_headerEl.getBoundingClientRect().height) : 0;
    const _mainH   = Math.max(mobilePageHeight - _headerH, 1);
    const _mainSnap = _zoneSnapshot(_mainEl, mobilePageWidth, _mainH, mobilePageMap);

    // ── ConteX Law — per-zone CSS capture ────────────────────────────────────
    // Called AFTER zone detection so each root element is known.
    // extractComponentCSS scopes selectorMatchesComponent() to the zone's own
    // element set — only rules that match elements within that zone are kept.
    // Chrome-sent zones produce null so the server receives no stale CSS for
    // components it already has on disk from the first extraction.
    let _headerCSS = null;
    let _navbarCSS  = null;
    let _mainCSS   = null;
    try {
        if (_headerEl) {
            _headerCSS = extractComponentCSS(_headerEl);
            console.log(`[SnapStak] Header CSS: ${_headerCSS.matched.length} matched, ${_headerCSS.media.length} media blocks`);
        }
        if (_navbarEl) {
            _navbarCSS = extractComponentCSS(_navbarEl);
            console.log(`[SnapStak] Navbar CSS: ${_navbarCSS.matched.length} matched, ${_navbarCSS.media.length} media blocks`);
        }
        if (_mainEl) {
            _mainCSS = extractComponentCSS(_mainEl);
            console.log(`[SnapStak] Main CSS: ${_mainCSS.matched.length} matched, ${_mainCSS.media.length} media blocks`);
        }
    } catch (_zoneCSSErr) {
        console.warn('[SnapStak] Per-zone CSS capture failed (non-fatal):', _zoneCSSErr.message);
    }

    // ── Chrome sent tracking ──────────────────────────────────────────────────
    // Header and navbar are static — only send once per app session.
    // Set window.__snapstak_chrome_sent__ = true after first send.
    // C# checks result.chromeSent to know whether to skip header/navbar POSTs.
    const _chromePreviouslySent = !!window.__snapstak_chrome_sent__;

    return {
        success: true,
        // Three separate domSnapshots — one per component
        headerSnapshot: _chromePreviouslySent ? null : _headerSnap,
        mainSnapshot:   _mainSnap,
        navbarSnapshot: _chromePreviouslySent ? null : _navbarSnap,
        // Legacy single snapshot (main only) for backwards compatibility
        domSnapshot: _mainSnap,
        hiddenComponents,
        // Per-zone CSS — each structural zone carries only its own scoped rules.
        // componentCSS kept as alias for mainCSS for server backwards compatibility.
        // Section-level CSS travels inside mobilePageMap[].cssB64 (per-landmark).
        headerCSS:    _chromePreviouslySent ? null : _headerCSS,
        navbarCSS:    _chromePreviouslySent ? null : _navbarCSS,
        mainCSS:      _mainCSS,
        componentCSS: _mainCSS,
        componentJS,
        chromeSent: _chromePreviouslySent,
    };
}

function serializeDOM(svgSymbolCache = new Map(), rootEl = null) {
    const MAX_DEPTH = 30;
    const visible = [];
    const invisible = [];
    let counter = 0;

    // SVG deduplication: track captured SVG data URIs to prevent same icon
    // appearing twice when the DOM has duplicate SVG nodes (e.g. logo in both
    // sticky header AND mobile nav, or spinner siblings at same position).
    // Key: svgDataURI string — if two SVGs produce identical markup they are the same icon.
    const _svgMarkupSeen = new Set();

    // Snapshot scroll position ONCE — all rects are relative to this
    const scrollX = window.scrollX || 0;
    const scrollY = window.scrollY || 0;
    console.log('[SnapStak] Serializing at scrollY=' + scrollY);

    // Track all text strings already captured — prevents duplicates across the tree

    const TEXT_TAGS = new Set([
        'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
        'p', 'span', 'strong', 'em', 'b', 'i', 'u',
        'label', 'legend', 'dt', 'dd',
        'th', 'td', 'caption',
        'blockquote', 'code', 'pre', 'time',
    ]);

    const VOID_TAGS = new Set([
        'input', 'img', 'video', 'audio', 'hr', 'br',
        'meta', 'link', 'source', 'track', 'wbr', 'embed',
    ]);

    // Tags we never want to capture at all
    const SKIP_TAGS = new Set([
        'script', 'noscript', 'style', 'head', 'html',
        'meta', 'link', 'title', 'base',
    ]);

    // Transparent container tags — no box model of their own in any browser.
    // getBoundingClientRect() returns 0×0 for these even when their children
    // are fully visible. We must NOT create an entry for them — they would land
    // in invisible[] and sever the parent chain for their children in StructureService.
    // Instead, recurse into their children passing the incoming parentId unchanged
    // so children attach directly to the nearest real ancestor in the SVG tree.
    // <picture>: selects between <source> variants — the <img> child is the
    //            rendered element. <picture> itself has no rendered box.
    const TRANSPARENT_TAGS = new Set(['picture']);

    function getDirectText(el) {
        let t = '';
        for (const n of el.childNodes) {
            if (n.nodeType === Node.TEXT_NODE) t += n.textContent;
        }
        return t.trim().slice(0, 300);
    }

    function serialize(el, depth, parentId) {
        if (depth > MAX_DEPTH) return;
        // SVGSVGElement is NOT an HTMLElement — it inherits from SVGElement.
        // Without this check, every <svg> element is silently skipped before
        // the tag === 'svg' handler at line 656 ever gets a chance to run.
        // Allow HTMLElement (all normal tags) AND SVGSVGElement (<svg> tags).
        if (!(el instanceof HTMLElement) && !(el instanceof SVGSVGElement)) return;

        const tag = el.tagName.toLowerCase();
        if (SKIP_TAGS.has(tag)) return;

        // Skip spinners/loaders — animated elements that are loading state UI, not content.
        // Detect by: infinite CSS animation OR classname matching spinner/loader patterns.
        // Check both the element itself AND its parent (spinner wrapper divs often hold the svg).
        const _isSpinner = (() => {
            const _cls = (typeof el.className === 'string' ? el.className : '') +
                (el.parentElement && typeof el.parentElement.className === 'string' ? ' ' + el.parentElement.className : '');
            if (/spinner|loading|loader|skeleton|spin/i.test(_cls)) return true;
            const _cs = window.getComputedStyle(el);
            if (_cs.animationName && _cs.animationName !== 'none' &&
                _cs.animationIterationCount === 'infinite') return true;
            // Also check parent element animation
            if (el.parentElement) {
                const _pcs = window.getComputedStyle(el.parentElement);
                if (_pcs.animationName && _pcs.animationName !== 'none' &&
                    _pcs.animationIterationCount === 'infinite') return true;
            }
            return false;
        })();
        if (_isSpinner) return;

        // Transparent pass-through tags — no box model, must not create an entry.
        // Recurse into children passing the SAME parentId so children attach to
        // the nearest real ancestor. Prevents <img> inside <picture> from becoming
        // a detached root node in StructureService when <picture>'s 0×0 rect
        // causes it to land in invisible[] and disappear from the element list.
        if (TRANSPARENT_TAGS.has(tag)) {
            for (const child of el.children) {
                serialize(child, depth + 1, parentId);
            }
            return;
        }

        // SVG elements — resolve sprite references and embed as data URI
        if (tag === 'svg') {
            const svgRect = el.getBoundingClientRect();
            const cs0 = window.getComputedStyle(el);

            // ── Skip hidden SVG elements ──────────────────────────────────────
            // The isHidden gate below only runs for non-svg tags. SVG icons that
            // are display:none, inside a hidden nav drawer, or off-screen at x=0
            // with no valid page position must be filtered here before capture.
            if (cs0.display === 'none' || cs0.visibility === 'hidden') return;
            // Skip if any ancestor is display:none (collapsed mobile nav, hidden drawer)
            {
                let _svgAnc = el.parentElement;
                let _svgAncDepth = 0;
                while (_svgAnc && _svgAnc !== document.body && _svgAncDepth < 10) {
                    const _svgAncCs = window.getComputedStyle(_svgAnc);
                    if (_svgAncCs.display === 'none' || _svgAncCs.visibility === 'hidden') return;
                    _svgAnc = _svgAnc.parentElement;
                    _svgAncDepth++;
                }
            }
            // getBoundingClientRect returns 0 for SVG icons inside collapsed accordion rows
            // even after expansion, because the parent container hasn't reflowed yet.
            // Fall back to the width/height attributes on the SVG element itself — these are
            // the authored dimensions and are always correct for sprite icons.
            const _svgW = svgRect.width > 1 ? svgRect.width
                : parseFloat(el.getAttribute('width')) || parseFloat(cs0.width) || 0;
            const _svgH = svgRect.height > 1 ? svgRect.height
                : parseFloat(el.getAttribute('height')) || parseFloat(cs0.height) || 0;
            if (_svgW > 1 && _svgH > 1 && cs0.display !== 'none') {
                // Deduplicate: skip if identical SVG markup was already captured.
                // Autosport has the logo in both the sticky header and the mobile nav —
                // both produce identical outerHTML so markup dedup catches it cleanly.
                // We check AFTER building svgMarkup below, before pushing to visible[].
                let svgMarkup = null;

                // Color source truth via canvas pixel sampling.
                // Instead of guessing which CSS property holds the icon color,
                // we ask the browser: what color did you ACTUALLY paint here?
                // Color: walk UP from the SVG's PARENT (not the SVG itself).
                // SVG elements have a UA-stylesheet color:black that overrides inheritance.
                // The actual white/grey color is on the containing button/anchor/div.
                // Skip black and transparent — first non-black ancestor color is the icon color.
                const _iconColor = (() => {
                    // 1. Check fill on the SVG element itself — getComputedStyle is the source of truth
                    const _svgCs = window.getComputedStyle(el);
                    const _svgFill = _svgCs.fill || '';
                    if (_svgFill && _svgFill !== 'rgb(0, 0, 0)' && _svgFill !== 'rgba(0, 0, 0, 0)'
                        && _svgFill !== 'none' && !_svgFill.startsWith('url(')) {
                        return _svgFill;
                    }
                    const _svgColor = _svgCs.color || '';
                    if (_svgColor && _svgColor !== 'rgb(0, 0, 0)' && _svgColor !== 'rgba(0, 0, 0, 0)') {
                        return _svgColor;
                    }
                    // 2. Walk ancestors — first non-black color wins
                    let _n = el.parentElement;
                    while (_n) {
                        const _ncs = window.getComputedStyle(_n);
                        const _nFill = _ncs.fill || '';
                        if (_nFill && _nFill !== 'rgb(0, 0, 0)' && _nFill !== 'rgba(0, 0, 0, 0)'
                            && _nFill !== 'none' && !_nFill.startsWith('url(')) {
                            return _nFill;
                        }
                        const _c = _ncs.color || '';
                        if (_c && _c !== 'rgb(0, 0, 0)' && _c !== 'rgba(0, 0, 0, 0)') return _c;
                        _n = _n.parentElement;
                    }
                    return 'rgb(240, 240, 240)'; // fallback white
                })();

                // Pattern: <use xlink:href="/path/sprite.svg#symbol-id">
                const useEl = el.querySelector('use');
                if (useEl) {
                    // Modern Chrome (90+) deprecated xlink:href — getAttribute('xlink:href')
                    // returns null even when the attribute exists in the markup.
                    // Must use getAttributeNS with the XLink namespace to read it correctly.
                    const XLINK = 'http://www.w3.org/1999/xlink';
                    const href = useEl.getAttributeNS(XLINK, 'href')
                        || useEl.getAttribute('href')
                        || useEl.getAttribute('xlink:href')
                        || '';
                    if (href.includes('.svg#') || (href.startsWith('#') && href.length > 1)) {
                        // Normalise to absolute URL — the prefetch cache keys are absolute,
                        // but the href in markup may be root-relative (/path/sprite.svg#id).
                        // Try absolute form first, fall back to raw href.
                        let absHref = href;
                        if (href.startsWith('/') || href.startsWith('./') || href.startsWith('../')) {
                            try { absHref = new URL(href, window.location.origin).href; } catch (e) { }
                        }
                        const cached = svgSymbolCache.get(absHref) || svgSymbolCache.get(href);
                        if (cached) {
                            // If the cached symbol has explicit fills it's a multi-colour logo —
                            // use it as-is. Only replace currentColor for line-art icons.
                            const _cachedHasExplicitFills = /\bfill\s*=\s*["'](?!none|currentColor)[^"']/i.test(cached);
                            if (_cachedHasExplicitFills) {
                                svgMarkup = cached;
                            } else {
                                const computedColor = _iconColor;
                                svgMarkup = cached
                                    .replace(/fill="currentColor"/gi, `fill="${computedColor}"`)
                                    .replace(/stroke="currentColor"/gi, `stroke="${computedColor}"`)
                                    .replace(/style="color:inherit;fill:currentColor;"/,
                                        `style="color:${computedColor};"`);
                            }
                            console.log('[SnapStak] Resolved sprite:', href, '| color:', _iconColor);
                        } else {
                            // Symbol not in cache — placeholder with exact browser-measured dimensions.
                            const w = Math.round(_svgW);
                            const h = Math.round(_svgH);
                            const _fs = Math.max(6, Math.round(Math.min(w, h) * 0.35));
                            svgMarkup = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}"`
                                + ` width="${w}" height="${h}">`
                                + `<rect width="${w}" height="${h}" rx="2" fill="#333333" stroke="#9333EA" stroke-width="1"/>`
                                + `<text x="${w / 2}" y="${h / 2}" font-family="sans-serif" font-size="${_fs}" fill="#9333EA"`
                                + ` text-anchor="middle" dominant-baseline="central">Sprite</text>`
                                + `</svg>`;
                            console.warn('[SnapStak] Sprite not cached:', href, '| absHref:', absHref, '| cache size:', svgSymbolCache.size, '| cache keys:', JSON.stringify([...svgSymbolCache.keys()].slice(0, 20)));
                        }
                    }
                }

                // Fallback: no <use>, embed outerHTML directly (works for inline SVGs like logos).
                // Replace only explicit currentColor — never set fill on root element.
                if (!svgMarkup) {
                    const computedColor = _iconColor;
                    // Strip shadow/blur decoration groups — they are drop-shadow artefacts
                    // that render as near-black smears when extracted without a backdrop.
                    // Identified by: opacity < 0.5 on the group AND all child paths also have opacity < 0.5
                    let _rawHTML = el.outerHTML;
                    _rawHTML = _rawHTML.replace(/<g[^>]*\sopacity="0\.\d+"[^>]*>[\s\S]*?<\/g>/g, (match) => {
                        // Only strip if ALL paths inside also have low opacity (pure shadow group)
                        const _pathOpacities = [...match.matchAll(/\sopacity="([\d.]+)"/g)].map(m => parseFloat(m[1]));
                        const _allLow = _pathOpacities.length > 0 && _pathOpacities.every(o => o <= 0.5);
                        return _allLow ? '' : match;
                    });
                    svgMarkup = _rawHTML
                        .replace(/fill="currentColor"/gi, `fill="${computedColor}"`)
                        .replace(/stroke="currentColor"/gi, `stroke="${computedColor}"`)
                        .replace(/rgb\(var\((--[^)]+)\)\)/g, (match, varName) => {
                            const val = window.getComputedStyle(document.documentElement)
                                .getPropertyValue(varName.trim()).trim();
                            return val ? `rgb(${val})` : match;
                        })
                        .replace(/var\((--[^)]+)\)/g, (match, varName) => {
                            const val = window.getComputedStyle(document.documentElement)
                                .getPropertyValue(varName.trim()).trim();
                            return val || match;
                        });
                }

                const svgDataURI = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svgMarkup);

                // Dedup: skip only if identical markup AND identical position.
                // Same icon used in multiple rows (e.g. accordion chevrons) MUST all be captured.
                // Only skip true duplicates — same SVG rendered at the exact same screen position.
                const _svgDedupKey = svgMarkup + '|' + Math.round(svgRect.left) + ',' + Math.round(svgRect.top);
                if (_svgMarkupSeen.has(_svgDedupKey)) return;
                _svgMarkupSeen.add(_svgDedupKey);

                visible.push({
                    internalId: 'el_' + (counter++),
                    tag: 'svg',
                    id: el.id || '',
                    className: typeof el.className === 'string' ? el.className : '',
                    isSVGIcon: true,
                    svgDataURI: svgDataURI,
                    hidden: false,
                    parentId: parentId,
                    cssProps: { color: cs0.color || '#ffffff', fill: cs0.fill || 'currentColor' },
                    rect: (() => {
                        // SVG icons inside carousel items: use the stamped absolute coords
                        // if present. Without this the icon reads svgRect at scroll=0
                        // and lands at x=0,y=0 for all off-screen carousel items.
                        const _svgStamp = el.getAttribute('data-snapstak-carousel-rect');
                        if (_svgStamp) {
                            try {
                                const s = JSON.parse(_svgStamp);
                                return { x: s.x, y: s.y, width: s.w, height: s.h };
                            } catch (_) { }
                        }
                        return {
                            x: Math.round(svgRect.left + (window.scrollX || 0)),
                            y: (cs0.position === 'fixed' || cs0.position === 'sticky')
                                ? Math.round(svgRect.top)
                                : Math.round(svgRect.top + (window.scrollY || 0)),
                            width: Math.round(_svgW),
                            height: Math.round(_svgH),
                        };
                    })(),
                });
            }
            return; // never recurse into SVG children
        }

        const cs = window.getComputedStyle(el);
        const _rawRect = el.getBoundingClientRect();

        // ── Carousel stamp override ───────────────────────────────────────────
        // expandHorizontalCarousels() stamps absolute page coords onto off-screen
        // carousel items before serializeDOM runs. If the stamp is present, use
        // those coords — the item was scrolled into view for accurate measurement.
        const _carouselStamp = el.getAttribute('data-snapstak-carousel-rect');
        const _stampedRect = _carouselStamp ? (() => {
            try {
                const s = JSON.parse(_carouselStamp);
                return {
                    left: s.x, top: s.y - (window.scrollY || 0), right: s.x + s.w,
                    bottom: s.y - (window.scrollY || 0) + s.h,
                    width: s.w, height: s.h, x: s.x, y: s.y
                };
            } catch (_) { return null; }
        })() : null;

        // getBoundingClientRect().height returns 0 for flex children with h-full when
        // the parent height comes from the flex container, not from content.
        // offsetHeight correctly resolves h-full — use it as fallback.
        const rect = _stampedRect ||
            ((_rawRect.height < 1 && _rawRect.width > 1 && el.offsetHeight > 0)
                ? {
                    left: _rawRect.left, top: _rawRect.top, right: _rawRect.right, bottom: _rawRect.bottom,
                    width: _rawRect.width, height: el.offsetHeight, x: _rawRect.x, y: _rawRect.y
                }
                : _rawRect);

        // Visible = has real pixel dimensions on screen
        // Invisible = zero size OR explicitly removed from layout (display:none)
        // Note: visibility:hidden and opacity:0 elements still occupy space — treat as visible
        const _semanticTags = new Set(['h1', 'h2', 'h3', 'h4', 'h5', 'h6',
            'p', 'span', 'strong', 'em', 'b', 'i', 'u', 'label', 'legend',
            'dt', 'dd', 'blockquote', 'figcaption']);
        const _tag = el.tagName ? el.tagName.toLowerCase() : '';
        const _hasZeroRect = rect.width < 1 && rect.height < 1;
        const _isAncestorHidden = _hasZeroRect && el.offsetParent === null
            && _tag !== 'body' && _tag !== 'html';
        // input/select/textarea are replaced elements — 0x0 rect even when visible.
        // Preserve them if not display:none so toggle switches are captured.
        const _isFormInput = _tag === 'input' || _tag === 'select' || _tag === 'textarea';

        // ── Thumb overlay check ───────────────────────────────────────────────
        // .ms-item__thumb-info-top and .ms-item__thumb-series are position:absolute
        // overlays rendered on top of article images via CSS absolute positioning.
        // SVG has no absolute positioning — they fall outside their container and
        // render as floating text above images. The HTML confirms this structure:
        // both elements sit directly inside .ms-item__thumb with position:absolute.
        // Skip any element whose computed position is absolute AND whose direct
        // parent contains the class ms-item__thumb — that combination is always
        // an image overlay with no valid SVG representation.
        let _isThumbAbsOverlay = false;
        if (cs.position === 'absolute') {
            const _p = el.parentElement;
            if (_p && typeof _p.className === 'string' && _p.className.includes('ms-item__thumb')) {
                _isThumbAbsOverlay = true;
            }
        }

        const isHidden =
            cs.display === 'none' ||
            _isAncestorHidden ||
            _isThumbAbsOverlay ||
            (_hasZeroRect && !_semanticTags.has(_tag) && !_isFormInput);

        let borderRadiusPx = 0;
        const brStr = cs.borderRadius || cs.borderTopLeftRadius || '';
        const brMatch = brStr.match(/([\d.]+)(%|px)/);
        if (brMatch) {
            borderRadiusPx = brMatch[2] === '%'
                ? (parseFloat(brMatch[1]) / 100) * Math.min(rect.width, rect.height)
                : parseFloat(brMatch[1]);
        }

        const internalId = 'el_' + (counter++);

        // Capture CSS visual properties in the same pass — no second round trip needed
        // cssProps — only non-default values stored. Browser defaults carry zero
        // signal and are stripped here to keep the payload lean.
        // borderTop/TopColor/TopWidth removed — border shorthand is sufficient.
        const cssProps = {};
        if (cs.backgroundColor) cssProps.backgroundColor = cs.backgroundColor;
        if (cs.backgroundImage && cs.backgroundImage !== 'none') cssProps.backgroundImage = cs.backgroundImage;
        if (cs.border) cssProps.border = cs.border;
        if (cs.borderRadius && cs.borderRadius !== '0px') cssProps.borderRadius = cs.borderRadius;
        if (cs.boxShadow && cs.boxShadow !== 'none') cssProps.boxShadow = cs.boxShadow;
        if (cs.color) cssProps.color = cs.color;
        if (cs.fontFamily) cssProps.fontFamily = cs.fontFamily;
        if (cs.fontSize) cssProps.fontSize = cs.fontSize;
        if (cs.fontWeight) cssProps.fontWeight = cs.fontWeight;
        if (cs.fontStyle && cs.fontStyle !== 'normal') cssProps.fontStyle = cs.fontStyle;
        if (cs.lineHeight) cssProps.lineHeight = cs.lineHeight;
        if (cs.letterSpacing && cs.letterSpacing !== 'normal') cssProps.letterSpacing = cs.letterSpacing;
        // Placeholder — overwritten after _ownsLine is computed below.
        cssProps.textAlign = cs.textAlign || '';
        if (cs.textTransform && cs.textTransform !== 'none') cssProps.textTransform = cs.textTransform;
        if (cs.textDecoration && cs.textDecoration !== 'none') cssProps.textDecoration = cs.textDecoration;
        if (cs.whiteSpace && cs.whiteSpace !== 'normal') cssProps.whiteSpace = cs.whiteSpace;
        if (cs.display) cssProps.display = cs.display;
        if (cs.alignItems && cs.alignItems !== 'normal'
            && cs.alignItems !== 'stretch') cssProps.alignItems = cs.alignItems;
        if (cs.justifyContent && cs.justifyContent !== 'normal') cssProps.justifyContent = cs.justifyContent;
        if (cs.flexDirection && cs.flexDirection !== 'row') cssProps.flexDirection = cs.flexDirection;
        if (cs.gap && cs.gap !== 'normal' && cs.gap !== '0px') cssProps.gap = cs.gap;
        if (cs.padding && cs.padding !== '0px') cssProps.padding = cs.padding;
        if (cs.paddingTop && cs.paddingTop !== '0px') cssProps.paddingTop = cs.paddingTop;
        if (cs.paddingBottom && cs.paddingBottom !== '0px') cssProps.paddingBottom = cs.paddingBottom;
        cssProps.paddingLeft = cs.paddingLeft || '';                     // always kept — used for text indent
        if (cs.paddingRight && cs.paddingRight !== '0px') cssProps.paddingRight = cs.paddingRight;
        if (cs.opacity && cs.opacity !== '1') cssProps.opacity = cs.opacity;
        if (cs.position && cs.position !== 'static') cssProps.position = cs.position;
        if (cs.zIndex && cs.zIndex !== 'auto') cssProps.zIndex = cs.zIndex;
        if (cs.overflow && cs.overflow !== 'visible') cssProps.overflow = cs.overflow;
        if (cs.objectFit && cs.objectFit !== 'fill') cssProps.objectFit = cs.objectFit;
        if (cs.alignSelf && cs.alignSelf !== 'auto') cssProps.alignSelf = cs.alignSelf;
        if (cs.flexGrow && cs.flexGrow !== '0') cssProps.flexGrow = cs.flexGrow;
        if (cs.flexShrink && cs.flexShrink !== '1') cssProps.flexShrink = cs.flexShrink;
        // Margin — only kept for margin:auto centering detection
        if (cs.marginLeft && cs.marginLeft !== '0px') cssProps.marginLeft = cs.marginLeft;
        if (cs.marginRight && cs.marginRight !== '0px') cssProps.marginRight = cs.marginRight;
        // CSS Anchor Positioning — captured in cssProps as well as the dedicated entry fields
        // so the server's SVG serializer can write them as data-* attributes on the SVG group.
        const _anchorName = cs.getPropertyValue('anchor-name').trim();
        const _positionAnchor = cs.getPropertyValue('position-anchor').trim();
        if (_anchorName && _anchorName !== 'none') cssProps.anchorName = _anchorName;
        if (_positionAnchor && _positionAnchor !== 'none') cssProps.positionAnchor = _positionAnchor;

        // Owns the full inline line — used for textContent capture AND recursion guard.
        const _ownsLine = (() => {
            if (tag === 'h1' || tag === 'h2' || tag === 'h3' || tag === 'h4' || tag === 'h5' || tag === 'h6'
                || tag === 'p' || tag === 'div' || tag === 'li') {
                // Block/heading elements always own their line — textAlign is intentional
                if (el.children.length === 0) return true;
            }
            if (el.children.length === 0) return false;
            if (tag === 'td' || tag === 'th' || tag === 'tr' || tag === 'table'
                || tag === 'tbody' || tag === 'thead' || tag.indexOf('-') !== -1) return false;
            for (let _i = 0; _i < el.children.length; _i++) {
                const _cd = window.getComputedStyle(el.children[_i]).display;
                const _ct = (el.children[_i].tagName || '').toLowerCase();
                // SVG children must always be recursed into — never treat parent as line owner
                if (_ct === 'svg') return false;
                if (_ct.indexOf('-') !== -1 || (_cd !== 'inline' && _cd !== 'inline-block' && _cd !== 'none'))
                    return false;
            }
            return true;
        })();

        // Now that _ownsLine is known, compute the correct textAlign.
        // _ownsLine elements are layout owners — their textAlign is intentional.
        // Other elements: only use textAlign if it differs from parent (prevents deep cascade).
        cssProps.textAlign = (() => {
            const _ta = cs.textAlign || '';
            if (!_ta || _ta === 'start') return '';
            if (el.style && el.style.textAlign) return _ta;
            if (_ownsLine) return _ta;
            if (!el.parentElement) return _ta;
            const _parentTa = window.getComputedStyle(el.parentElement).textAlign || '';
            return _ta !== _parentTa ? _ta : '';
        })();

        const entry = {
            internalId,
            parentId: parentId || null,
            tag,
            id: el.id || '',
            className: typeof el.className === 'string' ? el.className : '',
            role: el.getAttribute('role') || '',
            ariaLabel: el.getAttribute('aria-label') || '',
            // Extended ARIA — interactive state attributes the AI needs for accessible markup
            ariaExpanded: el.getAttribute('aria-expanded') || '',
            ariaHaspopup: el.getAttribute('aria-haspopup') || '',
            ariaControls: el.getAttribute('aria-controls') || '',
            ariaCurrent: el.getAttribute('aria-current') || '',
            ariaSelected: el.getAttribute('aria-selected') || '',
            // Form / button semantics
            inputType: el.type || '',
            inputName: el.name || '',
            placeholder: el.placeholder || '',
            autocomplete: el.getAttribute('autocomplete') || '',
            checked: el.checked !== undefined ? el.checked : false,
            multiple: el.multiple !== undefined ? el.multiple : false,
            disabled: el.disabled || false,
            readonly: el.readOnly || false,
            required: el.required || false,
            // Link semantics
            target: el.getAttribute('target') || '',
            rel: el.getAttribute('rel') || '',
            // Site-specific data attributes — drive JS behaviour (e.g. data-button, data-command)
            dataAttributes: (() => {
                const _da = {};
                if (el.dataset) {
                    for (const [k, v] of Object.entries(el.dataset)) {
                        // Skip SnapStak internal attributes
                        if (k === 'segmentId' || k === 'responsive' || k === 'snapstkH') continue;
                        if (v !== undefined && v !== '') _da[k] = v;
                    }
                }
                return Object.keys(_da).length ? _da : null;
            })(),
            // ── ConteX Law — Structure Pillar: Browser-native declarative API attributes ──
            // These are standard HTML attributes that are NOT in el.dataset — they drive
            // browser-native behaviour (Popover API, Invoker Commands, Interest API) without
            // any JavaScript. They must be captured here on the element that owns them so the
            // code generator can emit the correct declarative HTML rather than JS polyfills.
            // CSS Anchor Positioning (anchor-name, position-anchor) is read via getComputedStyle
            // because it may be set via stylesheet rules OR inline style — both resolved here.
            popoverAttr: el.getAttribute('popover') || '',
            popoverTarget: el.getAttribute('popovertarget') || '',
            popoverAction: el.getAttribute('popoveraction') || '',
            commandFor: el.getAttribute('commandfor') || '',
            command: el.getAttribute('command') || '',
            interestFor: el.getAttribute('interestfor') || '',
            anchorName: window.getComputedStyle(el).getPropertyValue('anchor-name').trim() || '',
            positionAnchor: window.getComputedStyle(el).getPropertyValue('position-anchor').trim() || '',
            isTextNode: TEXT_TAGS.has(tag),
            // Capture direct text for ALL elements — use getDirectText() to avoid
            // duplicating text that belongs to child elements.
            textContent: (() => {
                // <select>: use selected option text only — innerText returns all options concatenated.
                if (tag === 'select' && el.options && el.selectedIndex >= 0) {
                    return el.options[el.selectedIndex].text.trim().slice(0, 500);
                }
                const ownText = (el.innerText || el.textContent || '').trim();
                if (!ownText) return '';

                // Only capture text on the DEEPEST owner — never duplicate parent text
                // Rule: if ALL of this element's text is already owned by a single child,
                // skip it here (the child will render it).
                if (el.children.length > 0) {
                    // If ALL children are inline (span, a, em etc.) this element owns the full line.
                    // Check this FIRST — before the single-child-owns-all check, which would
                    // incorrectly skip <li><a>text</a></li> and then _ownsLine stops recursion too.
                    const _notTable = tag !== 'td' && tag !== 'th' && tag !== 'tr' && tag !== 'table';
                    const _notCustom = tag.indexOf('-') === -1;
                    if (_notTable && _notCustom) {
                        let _allInline = el.children.length > 0;
                        for (let _ci = 0; _ci < el.children.length; _ci++) {
                            const _cd = window.getComputedStyle(el.children[_ci]).display;
                            const _ct = (el.children[_ci].tagName || '').toLowerCase();
                            if (_ct.indexOf('-') !== -1 || (_cd !== 'inline' && _cd !== 'inline-block' && _cd !== 'none')) {
                                _allInline = false; break;
                            }
                        }
                        if (_allInline) {
                            return ownText.slice(0, 500);
                        }
                    }
                    // Check if any single child contains ALL of this element's text
                    // (only reached when children are NOT all inline)
                    for (const child of el.children) {
                        const childText = (child.innerText || child.textContent || '').trim();
                        if (childText && childText === ownText) {
                            return ''; // child owns all this text — skip parent
                        }
                    }
                    // Only capture direct text nodes (text not inside any child element)
                    const dt = getDirectText(el);
                    if (!dt) return '';
                    return dt;
                }
                // No children - this element fully owns its text.
                return ownText.slice(0, 500);
            })(),
            src: el.currentSrc || el.src || el.getAttribute('src') || el.dataset.snapstkImgSrc || '',
            srcset: el.getAttribute('srcset') || el.getAttribute('data-srcset') || '',
            sizes: el.getAttribute('sizes') || el.getAttribute('data-sizes') || '',
            dataSrc: el.getAttribute('data-src') || el.getAttribute('data-lazy-src') || '',
            alt: el.alt || el.getAttribute('alt') || '',
            href: el.href || el.getAttribute('href') || '',
            // Capture inline children colors for mixed-color text rendering (e.g. red "Motorsport Network")
            // Only set when _ownsLine is true AND children have different colors.
            inlineChildren: (() => {
                if (!_ownsLine) return null;
                const kids = [];
                // Walk childNodes in DOM order — text nodes and element nodes exactly as the browser sees them.
                // Preserve whitespace from actual whitespace text nodes between elements.
                for (let _ni = 0; _ni < el.childNodes.length; _ni++) {
                    const _nd = el.childNodes[_ni];
                    if (_nd.nodeType === 3) { // TEXT_NODE — includes raw whitespace between elements
                        const _nt = _nd.textContent || '';
                        if (_nt.trim()) kids.push({ text: _nt, color: cs.color || '' });
                    } else if (_nd.nodeType === 1) { // ELEMENT_NODE
                        const _kt = (_nd.innerText || _nd.textContent || '');
                        if (_kt.trim()) kids.push({ text: _kt, color: window.getComputedStyle(_nd).color || '' });
                    }
                }
                const colors = [...new Set(kids.map(k => k.color))];
                return colors.length > 1 ? kids : null;
            })(),
            isResponsive: !!(el.getAttribute('srcset') || el.getAttribute('data-srcset')),

            // textNodeOffsetX: x offset of the direct text node within this element.
            // When a flex container has element children BEFORE a raw text node
            // (e.g. <a class="flex gap-2.5"><div class="w-48">logo</div> Formula 1</a>),
            // getBoundingClientRect() on the <a> gives x=0 (left edge of logo).
            // But the text "Formula 1" starts AFTER the logo div + gap.
            // Use Range.getBoundingClientRect() on the actual text node to get its
            // true x position — this IS the CSS source of truth, direct from the browser.
            textNodeOffsetX: (() => {
                // Only relevant when element has BOTH element children AND a direct text node
                if (el.children.length === 0) return 0;
                for (let _ni = 0; _ni < el.childNodes.length; _ni++) {
                    const _nd = el.childNodes[_ni];
                    if (_nd.nodeType === Node.TEXT_NODE && _nd.textContent.trim()) {
                        try {
                            const _range = document.createRange();
                            _range.selectNode(_nd);
                            const _tr = _range.getBoundingClientRect();
                            const _er = el.getBoundingClientRect();
                            // Offset of text node relative to element's left edge
                            const _offset = Math.round(_tr.left - _er.left);
                            return _offset > 0 ? _offset : 0;
                        } catch (_) { }
                    }
                }
                return 0;
            })(),

            // For <picture> elements — capture all <source> children
            pictureSources: tag === 'picture'
                ? Array.from(el.querySelectorAll('source')).map(s => ({
                    srcset: s.getAttribute('srcset') || '',
                    sizes: s.getAttribute('sizes') || '',
                    media: s.getAttribute('media') || '',
                    type: s.getAttribute('type') || '',
                }))
                : [],
            borderRadiusPx,
            hidden: isHidden,
            segmentId: el.dataset.segmentId || '',
            responsive: el.dataset.responsive === 'true',
            cssProps,
            parentCssProps: el.parentElement ? (() => {
                const _pcs = window.getComputedStyle(el.parentElement);
                return {
                    display: _pcs.display || '',
                    flexDirection: _pcs.flexDirection || '',
                    alignItems: _pcs.alignItems || '',
                    justifyContent: _pcs.justifyContent || '',
                    textAlign: _pcs.textAlign || '',
                    paddingLeft: _pcs.paddingLeft || '',
                    paddingRight: _pcs.paddingRight || '',
                    width: _pcs.width || '',
                    backgroundColor: _pcs.backgroundColor || '',
                };
            })() : null,
            // Walk UP the DOM to find the layout container that defines text column width.
            // PRIMARY: nearest ancestor with 'flex' (not 'inline-flex') in className
            //   AND no direct text node children — covers 90% of modern responsive layouts.
            // FALLBACK: nearest ancestor that is meaningfully wider than this element —
            //   handles non-flex containers: <ul>, <nav>, <address> parents, etc.
            parentRect: (() => {
                const _captureRect = (_el) => {
                    const _ar = _el.getBoundingClientRect();
                    if (_ar.width < 10) return null;
                    const _cs2 = window.getComputedStyle(_el);
                    const _pl = parseFloat(_cs2.paddingLeft) || 0;
                    const _pr2 = parseFloat(_cs2.paddingRight) || 0;
                    const _pt = parseFloat(_cs2.paddingTop) || 0;
                    const _pb = parseFloat(_cs2.paddingBottom) || 0;
                    return {
                        x: Math.round(_ar.left + scrollX),
                        y: Math.round(_ar.top + scrollY),
                        width: Math.round(_ar.width),
                        height: Math.round(_ar.height),
                        innerWidth: Math.round(_ar.width - _pl - _pr2),
                        innerHeight: Math.round(_ar.height - _pt - _pb),
                        paddingLeft: Math.round(_pl),
                        paddingTop: Math.round(_pt),
                    };
                };
                let _anc = el.parentElement;
                let _fallback = null;
                while (_anc && _anc !== document.body) {
                    const _tag = (_anc.tagName || '').toLowerCase();
                    const _isContainer = _tag === 'div' || _tag === 'section' || _tag === 'article'
                        || _tag === 'ul' || _tag === 'li' || _tag === 'nav'
                        || _tag === 'footer' || _tag === 'main' || _tag === 'header';
                    if (_isContainer) {
                        const _ar = _anc.getBoundingClientRect();
                        if (_ar.width > 10 && _ar.width <= rect.width + 2) {
                            const _r = _captureRect(_anc);
                            if (_r) return _r;
                        }
                        if (!_fallback && _ar.width > 10) {
                            _fallback = _captureRect(_anc);
                        }
                    }
                    _anc = _anc.parentElement;
                }
                return _fallback;
            })(),
            rect: (() => {
                // Zero-rect semantic tags (h3 etc mid-reflow): walk up DOM to find
                // nearest ancestor with a real rect for correct x/y/width/height.
                let _r = rect;
                if (_r.width < 1 && _r.height < 1 &&
                    typeof _semanticTags !== 'undefined' && _semanticTags.has(_tag)) {
                    let _anc = el.parentElement;
                    while (_anc && _anc !== document.body) {
                        const _ar = _anc.getBoundingClientRect();
                        if (_ar.width > 1 && _ar.height > 1) { _r = _ar; break; }
                        _anc = _anc.parentElement;
                    }
                }
                return {
                    x: Math.round(_r.left + scrollX),
                    y: (cs.position === 'fixed' || cs.position === 'sticky')
                        ? Math.round(_r.top)
                        : Math.round(_r.top + scrollY),
                    width: Math.round(_r.width),
                    height: Math.round(_r.height),
                };
            })(),
        };

        // Strip empty/falsy entry fields — only send signal, never noise.
        // Receiver uses || '' / || false defaults so missing fields are safe.
        for (const _k of Object.keys(entry)) {
            const _v = entry[_k];
            if (_v === '' || _v === false || _v === null || _v === 0 ||
                (Array.isArray(_v) && _v.length === 0)) {
                delete entry[_k];
            }
        }

        if (isHidden) {
            invisible.push(entry);
        } else {
            visible.push(entry);
        }

        if (!VOID_TAGS.has(tag) && tag !== 'svg' && !_ownsLine) {
            for (const child of el.children) {
                serialize(child, depth + 1, internalId);
            }
        }
    }

    serialize(rootEl || document.body, 0);

    return { visible, invisible };
}

function extractMeta() {
    return {
        url: window.location.href,
        title: document.title,
        viewport: {
            width: window.innerWidth || 1440,
            height: window.innerHeight || 900,
        },
        scrollWidth: document.documentElement.scrollWidth,
        scrollHeight: document.documentElement.scrollHeight,
        devicePixelRatio: window.devicePixelRatio || 1,
    };
}