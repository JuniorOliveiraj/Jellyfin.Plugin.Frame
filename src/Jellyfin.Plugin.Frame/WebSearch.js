(() => {
    'use strict';

    const scriptUrl = new URL(document.currentScript.src);
    const endpoint = new URL('./', scriptUrl);
    let active;
    let filter;

    const style = document.createElement('style');
    style.textContent = `
        #searchPage[data-frame-has-results] .noItemsMessage,
        #searchPage[data-frame-type="movie"] .searchResults .verticalSection:not([data-frame-library-movies]),
        #searchPage[data-frame-type="series"] .searchResults .verticalSection:not([data-frame-library-series]) {
            display: none;
        }
        #searchPage .frame-web-filter {
            display: flex;
            align-items: center;
            justify-content: center;
            gap: .6em;
            margin: .75em 0;
        }
        #searchPage .frame-web-filter select {
            font: inherit;
            border: 1px solid currentColor;
            border-radius: .35em;
            padding: .4em .65em;
            color: inherit;
            background: var(--theme-background-color, #202020);
        }
        #frame-web-results .frame-web-status:empty { display: none; }
        #frame-web-results .frame-web-cards,
        #searchPage[data-frame-vertical] :is([data-frame-library-movies], [data-frame-library-series]) .itemsContainer {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(min(100%, clamp(8rem, 14vw, 12rem)), 1fr));
            align-items: start;
        }
        #frame-web-results .frame-web-cards { margin: 0 -.6em; padding: 0; list-style: none; }
        #frame-web-results .frame-web-cards > .card,
        #searchPage[data-frame-vertical] :is([data-frame-library-movies], [data-frame-library-series]) .itemsContainer > .card {
            width: 100%;
            min-width: 0;
        }
        #searchPage[data-frame-vertical] .searchResults { padding-bottom: 0 !important; }
        #searchPage[data-frame-vertical] :is([data-frame-library-movies], [data-frame-library-series]) > .emby-scrollbuttons { display: none; }
        #searchPage[data-frame-vertical] :is([data-frame-library-movies], [data-frame-library-series]) > [is="emby-scroller"] {
            overflow: visible !important;
            touch-action: auto !important;
        }
        #searchPage[data-frame-vertical] :is([data-frame-library-movies], [data-frame-library-series]) .itemsContainer {
            width: auto !important;
            white-space: normal !important;
            transform: none !important;
        }
        #frame-web-results .frame-web-sentinel { height: 1px; }
        #frame-web-results .frame-web-feedback {
            margin: -.7em .6em 1em;
            font-size: .88em;
            white-space: normal;
            overflow-wrap: anywhere;
        }
        #frame-web-results .frame-web-feedback[hidden],
        #frame-web-results .frame-web-retry[hidden] { display: none; }
        #frame-web-results .frame-web-feedback .frame-web-status { margin: 0 0 .35em; }
        #frame-web-results .frame-web-open {
            width: 100%;
            border: 0;
            padding: 0;
            background: transparent;
            color: inherit;
            font: inherit;
            text-align: center;
            cursor: pointer;
        }
        #frame-web-results .frame-web-open:focus-visible {
            outline: 2px solid currentColor;
            outline-offset: -2px;
            border-radius: .4em;
        }
        #frame-web-results .frame-web-open:disabled { cursor: wait; }
        #frame-web-results .frame-web-open .cardScalable { position: relative; }
        #frame-web-results .frame-web-loading {
            display: none;
            position: absolute;
            inset: 0;
            z-index: 2;
            place-items: center;
            border-radius: inherit;
            background: rgb(0 0 0 / 58%);
            color: white;
            pointer-events: none;
        }
        #frame-web-results .frame-web-open[aria-busy="true"] .frame-web-loading,
        #frame-web-results [data-frame-importing] .frame-web-loading { display: grid; }
        #frame-web-results .frame-web-loading::before {
            content: '';
            box-sizing: border-box;
            width: 2rem;
            height: 2rem;
            border: .2rem solid rgb(255 255 255 / 34%);
            border-top-color: currentColor;
            border-radius: 50%;
            animation: frame-web-spin .75s linear infinite;
        }
        @keyframes frame-web-spin { to { transform: rotate(360deg); } }
        @media (prefers-reduced-motion: reduce) {
            #frame-web-results .frame-web-loading::before { animation: none; }
        }
        #frame-web-results .frame-web-open .cardBox,
        #frame-web-results .frame-web-open .cardScalable,
        #frame-web-results .frame-web-open .cardPadder,
        #frame-web-results .frame-web-open .cardText { display: block; }
        #frame-web-results .frame-web-poster { display: block; width: 100%; height: 100%; object-fit: cover; }
    `;
    document.head.append(style);

    function requestClient() {
        const api = window.ApiClient;
        const token = api?.accessToken?.();
        if (!token || typeof api.getUrl !== 'function') return null;
        const url = new URL(api.getUrl('Frame/Browse/WebScript'), location.href);
        if (url.origin !== endpoint.origin || url.pathname !== scriptUrl.pathname) return null;
        return { api, token };
    }

    async function request({ token }, route, query, options = {}) {
        const url = new URL(route, endpoint);
        if (query) url.search = new URLSearchParams(query);
        const headers = { 'X-Emby-Token': token };
        if (options.body) headers['Content-Type'] = 'application/json';
        const response = await fetch(url, { cache: 'no-store', redirect: 'error', ...options, headers });
        const data = await response.json().catch(() => null);
        const message = data?.message || data?.Message;
        if (!message && [401, 403, 404].includes(response.status)) return null;
        if (!response.ok) throw new Error(message || 'O Frame não respondeu. Tente novamente.');
        return data;
    }

    function button(label, action) {
        const element = document.createElement('button');
        element.type = 'button';
        element.className = 'emby-button raised';
        element.textContent = label;
        element.addEventListener('click', action);
        return element;
    }

    const languageMarker = 'LEG(?:ENDAD[OA])?|DUB(?:LAD[OA])?|DUAL(?:[ -]+[AÁ]UDIO)?';
    const trailingLanguage = new RegExp(
        '\\s*(?:\\((' + languageMarker + ')\\)|\\[(' + languageMarker + ')\\]|[-–—]\\s*(' + languageMarker + '))\\s*$', 'i');

    function titleLanguage(title) {
        const match = title.match(trailingLanguage);
        if (!match || match.index === 0) return { title, language: '' };

        const marker = (match[1] || match[2] || match[3]).toUpperCase();
        const language = marker.startsWith('LEG') ? 'Legendado' : marker.startsWith('DUB') ? 'Dublado' : 'Dual áudio';
        return { title: title.slice(0, match.index).trimEnd(), language };
    }

    function titleLabel(item) {
        let label = titleLanguage(item.name.trim());
        const suffix = label.title.match(/\s+(?:[-–—]\s*(\d{4})|\((\d{4})\))(\s*\([^()]+\))?\s*$/);
        const titleYear = suffix ? Number(suffix[1] || suffix[2]) : 0;
        const year = item.year > 0 ? item.year : titleYear;
        if (suffix && year === titleYear) {
            const clean = titleLanguage((label.title.slice(0, suffix.index) + (suffix[3] || '')).trim());
            label = { title: clean.title, language: label.language || clean.language };
        }
        return { ...label, year };
    }

    function addPoster(container, url, onError) {
        const cover = document.createElement('span');
        cover.className = 'cardContent cardImageContainer';
        const poster = document.createElement('img');
        poster.className = 'frame-web-poster';
        poster.alt = '';
        poster.loading = 'lazy';
        poster.decoding = 'async';
        poster.referrerPolicy = 'no-referrer';
        poster.addEventListener('error', () => {
            cover.remove();
            onError?.();
        }, { once: true });
        poster.src = url;
        cover.append(poster);
        container.append(cover);
    }

    function observePoster(state, card, container, item) {
        if (active !== state || !window.IntersectionObserver) return;
        state.posterObserver ??= new IntersectionObserver(entries => {
            for (const { target, isIntersecting } of entries) {
                if (!isIntersecting || active !== state) continue;
                const load = state.pendingPosters.get(target);
                state.pendingPosters.delete(target);
                state.posterObserver.unobserve(target);
                load?.();
            }
        });

        state.pendingPosters.set(card, async () => {
            try {
                const data = await request(state.client, 'WebPoster', { externalId: item.externalId },
                    { signal: state.viewAbort.signal });
                if (active === state && card.isConnected && data?.posterUrl && data.posterUrl !== item.posterUrl) {
                    addPoster(container, data.posterUrl);
                }
            } catch {
                // Keep the placeholder when the cached poster is unavailable.
            }
        });
        state.posterObserver.observe(card);
    }

    function postImport(state, item, page, retryPending = false) {
        return request(state.client, 'WebImport', null, {
            method: 'POST',
            body: JSON.stringify({ term: state.term, page, externalId: item.externalId,
                isSeries: item.isSeries === true, retryPending })
        });
    }

    function seriesProgress(state, item, page, card, feedback) {
        const text = document.createElement('p');
        text.className = 'frame-web-status';
        text.setAttribute('role', 'status');

        let current;
        let timer;
        let navigating = false;
        let retrying = false;
        const canRetry = () => ['failed', 'partial'].includes(current?.status) && current.pending > 0;
        const schedule = () => {
            const importing = ['queued', 'importing'].includes(current?.status);
            card.toggleAttribute('data-frame-importing', importing || retrying);
            clearTimeout(timer);
            if (active === state && !navigating && !retrying && importing)
                timer = setTimeout(poll, 2500);
        };

        const retry = button('Tentar episódios pendentes', async () => {
            if (active !== state || navigating || retrying || !canRetry()) return;

            retrying = true;
            retry.disabled = true;
            text.textContent = 'Solicitando a importação dos episódios pendentes…';
            schedule();
            try {
                const result = await postImport(state, item, page, true);
                if (active !== state) return;
                if (!result?.importProgress) throw new Error('Não foi possível repetir os episódios pendentes.');
                update(result.importProgress);
            } catch (error) {
                if (active === state) text.textContent = error.message;
            } finally {
                retrying = false;
                retry.disabled = navigating;
                schedule();
            }
        });
        retry.classList.add('frame-web-retry', 'frame-web-pending');
        retry.setAttribute('aria-label', 'Tentar episódios pendentes de ' + item.name);
        retry.hidden = true;
        feedback.append(text, retry);

        function update(progress) {
            if (!progress || active !== state) return;
            current = progress;
            feedback.hidden = false;

            const parts = [];
            if (progress.status === 'queued') parts.push('Importação na fila');
            if (progress.total > 0) parts.push(`${progress.imported} de ${progress.total} episódios importados`);
            else if (progress.status === 'importing') parts.push('Consultando episódios');
            if (canRetry()) parts.push(`${progress.pending} ${progress.pending === 1 ? 'pendente' : 'pendentes'}`);
            if (progress.message) parts.push(progress.message);
            const label = parts.join(' · ');
            if (text.textContent !== label) text.textContent = label;
            retry.hidden = !canRetry();
            schedule();
        }

        async function poll() {
            if (active !== state || navigating || retrying) return;
            try {
                const result = await request(state.client, 'WebSeries', { externalId: item.externalId },
                    { signal: state.viewAbort.signal });
                if (!navigating && !retrying) update(result?.importProgress);
            } catch {
                schedule();
            }
        }

        update(item.importProgress);
        return {
            update,
            canRetry,
            setNavigating(value) {
                navigating = value;
                retry.disabled = navigating || retrying;
                schedule();
            }
        };
    }

    function titleCard(state, item, page) {
        const { title, year, language } = titleLabel(item);
        const metadata = [year, item.isSeries ? 'Série' : 'Filme', language].filter(Boolean).join(' · ');
        const card = document.createElement('li');
        card.className = 'card overflowPortraitCard';

        const start = () => openTitle(state, item, page, { open, feedback, status, retry, progress });
        const open = button('', start);
        open.className = 'frame-web-open';
        open.disabled = !!state.opening;
        open.setAttribute('aria-label', 'Abrir ' + title + (metadata ? ' (' + metadata + ')' : '') + ' no Jellyfin');

        const feedback = document.createElement('div');
        feedback.className = 'frame-web-feedback';
        feedback.hidden = true;
        const status = document.createElement('p');
        status.className = 'frame-web-status';
        status.setAttribute('role', 'status');
        const retry = button('Tentar novamente', start);
        retry.classList.add('frame-web-retry');
        retry.setAttribute('aria-label', 'Tentar novamente: ' + title + (metadata ? ' (' + metadata + ')' : ''));
        retry.hidden = true;
        feedback.append(status, retry);
        const progress = item.isSeries ? seriesProgress(state, item, page, card, feedback) : null;

        const box = document.createElement('span');
        box.className = 'cardBox cardBox-bottompadded';
        const scalable = document.createElement('span');
        scalable.className = 'cardScalable';
        const padder = document.createElement('span');
        padder.className = 'cardPadder cardPadder-overflowPortrait';
        const placeholder = document.createElement('span');
        placeholder.className = 'cardImageIcon material-icons ' + (item.isSeries ? 'tv' : 'movie');
        placeholder.setAttribute('aria-hidden', 'true');
        padder.append(placeholder);
        const loading = document.createElement('span');
        loading.className = 'frame-web-loading';
        loading.setAttribute('aria-hidden', 'true');
        scalable.append(padder, loading);

        const findPoster = () => observePoster(state, card, scalable, item);
        if (item.posterUrl) {
            addPoster(scalable, item.posterUrl, findPoster);
        } else {
            findPoster();
        }

        const name = document.createElement('span');
        name.className = 'cardText cardText-first';
        name.textContent = title;
        name.title = title;
        box.append(scalable, name);
        if (metadata) {
            const details = document.createElement('span');
            details.className = 'cardText cardText-secondary secondaryText';
            details.textContent = metadata;
            box.append(details);
        }

        open.append(box);
        card.append(open, feedback);
        return card;
    }

    function createSection() {
        const section = document.createElement('section');
        section.id = 'frame-web-results';
        section.className = 'padded-left padded-right padded-bottom-page';
        section.setAttribute('aria-labelledby', 'frame-web-heading');
        section.hidden = true;

        const heading = document.createElement('h2');
        heading.id = 'frame-web-heading';
        heading.className = 'sectionTitle sectionTitle-cards';
        heading.textContent = 'Resultados do Frame';
        const status = document.createElement('p');
        status.setAttribute('role', 'status');
        status.className = 'frame-web-status';
        const list = document.createElement('ul');
        list.className = 'frame-web-cards';
        const footer = document.createElement('div');
        footer.setAttribute('aria-live', 'polite');
        const sentinel = document.createElement('div');
        sentinel.className = 'frame-web-sentinel';
        sentinel.setAttribute('aria-hidden', 'true');
        section.append(heading, status, list, footer, sentinel);
        return { section, status, list, footer, sentinel };
    }

    function showFilter(state) {
        if (!filter) {
            const label = document.createElement('label');
            label.className = 'frame-web-filter';
            label.append('Tipo de conteúdo');
            const select = document.createElement('select');
            for (const [value, text] of [['', 'Todos'], ['movie', 'Filmes'], ['series', 'Séries']]) {
                const option = document.createElement('option');
                option.value = value;
                option.textContent = text;
                select.append(option);
            }
            select.addEventListener('change', syncSearch);
            label.append(select);
            filter = { field: state.field, label, select };
        }
        state.page.setAttribute('data-frame-vertical', '');
        state.page.setAttribute('data-frame-type', state.type);
        placeAfterLibrary(state);
    }

    function clearFilter() {
        filter?.label.remove();
        filter = undefined;
    }

    function placeAfterLibrary(state) {
        const fields = state.field.closest('.searchFields');
        const parent = fields.parentElement;
        if (parent.lastElementChild !== state.section) parent.append(state.section);
        if (filter?.field === state.field && filter.label.parentElement !== fields) fields.append(filter.label);

        if (!state.page.hasAttribute('data-frame-vertical')) return;
        for (const row of state.page.querySelectorAll('.searchResults .verticalSection')) {
            row.toggleAttribute('data-frame-library-movies', !!row.querySelector('.card[data-type="Movie"]'));
            row.toggleAttribute('data-frame-library-series', !!row.querySelector('.card[data-type="Series"]'));
        }
    }

    function reset() {
        if (!active) return;
        clearTimeout(active.queryTimer);
        active.searchRequest?.abort();
        active.observer?.disconnect();
        active.posterObserver?.disconnect();
        active.viewAbort.abort();
        active.page.removeAttribute('data-frame-vertical');
        active.page.removeAttribute('data-frame-has-results');
        active.page.removeAttribute('data-frame-type');
        active.section.remove();
        active = undefined;
    }

    function observeNextPage(state) {
        if (!state.hasNext) return;
        if (!window.IntersectionObserver) {
            state.footer.append(button('Carregar mais resultados', () => loadNextPage(state)));
            return;
        }

        state.observer ??= new IntersectionObserver(entries => {
            if (entries.some(entry => entry.isIntersecting)) loadNextPage(state);
        }, { rootMargin: '0px 0px 240px' });
        state.observer.observe(state.sentinel);
    }

    async function loadNextPage(state) {
        if (active !== state || state.searchRequest || !state.hasNext) return;

        state.observer?.unobserve(state.sentinel);
        const controller = new AbortController();
        state.searchRequest = controller;
        const page = state.nextPage;
        state.footer.textContent = page === 1 ? 'Buscando no Frame…' : 'Carregando mais resultados…';

        try {
            const query = { term: state.term, page };
            if (state.type) query.type = state.type;
            const data = await request(state.client, 'WebSearch', query, { signal: controller.signal });
            if (active !== state || controller.signal.aborted) return;
            if (!data) {
                state.hasNext = false;
                state.section.hidden = true;
                state.page.removeAttribute('data-frame-vertical');
                state.page.removeAttribute('data-frame-has-results');
                state.page.removeAttribute('data-frame-type');
                clearFilter();
                return;
            }
            if (!Array.isArray(data.items)) throw new Error('O Frame devolveu uma resposta de busca inválida.');

            const repeated = data.pageKey && state.seenPages.has(data.pageKey);
            if (data.pageKey) state.seenPages.add(data.pageKey);
            if (!repeated) {
                for (const item of data.items) {
                    const key = (item.isSeries ? 'series:' : 'movie:') + item.externalId;
                    if (state.seenTitles.has(key)) continue;
                    state.seenTitles.add(key);
                    state.list.append(titleCard(state, item, page));
                }
            }

            state.hasNext = data.canPageForward === true && !repeated;
            state.nextPage = page + 1;
            state.section.hidden = false;
            state.page.toggleAttribute('data-frame-has-results', state.seenTitles.size > 0);
            showFilter(state);
            state.footer.textContent = '';
            if (!state.hasNext && state.seenTitles.size === 0) {
                state.status.textContent = 'Nenhum resultado adicional no Frame.';
            }
            observeNextPage(state);
        } catch (error) {
            if (active !== state || controller.signal.aborted) return;
            state.section.hidden = false;
            state.footer.replaceChildren(document.createTextNode(error.message + ' '),
                button('Tentar novamente', () => loadNextPage(state)));
        } finally {
            state.searchRequest = null;
        }
    }

    function setOpening(state, opening) {
        state.opening = opening;
        for (const control of state.list.querySelectorAll('button:not(.frame-web-pending)')) control.disabled = opening;
    }

    async function refreshDetailsWhenArtworkArrives(client, item, itemId, detailsHash) {
        const controller = new AbortController();
        const { signal } = controller;
        const timeout = setTimeout(() => controller.abort(), 80000);
        const onDetails = () => location.hash === detailsHash;
        window.addEventListener('hashchange', () => {
            if (!onDetails()) controller.abort();
        }, { signal });
        document.addEventListener('click', event => {
            if (event.target?.closest?.('#itemDetailPage button, .dialogContainer')) controller.abort();
        }, { capture: true, signal });

        try {
            let delay = 600;
            while (!signal.aborted && onDetails()) {
                await new Promise(resolve => setTimeout(resolve, delay));
                delay = 1500;
                if (signal.aborted || !onDetails()) return;

                let status;
                try {
                    status = await request(client, item.isSeries ? 'WebSeries' : 'WebMovie',
                        { externalId: item.externalId }, { signal });
                } catch {
                    continue;
                }
                if (status?.itemId !== itemId) return;
                if (status.primaryImageReady === true && !signal.aborted && onDetails()) {
                    const details = new URLSearchParams(detailsHash.split('?')[1]);
                    // A new route query asks the native view to read the item again and preserves Back.
                    details.set('frameArtwork', Date.now().toString(36));
                    location.replace('#/details?' + details);
                    return;
                }
            }
        } finally {
            clearTimeout(timeout);
            controller.abort();
        }
    }

    async function openTitle(state, item, page, { open, feedback, status, retry, progress }) {
        if (active !== state || state.opening) return;

        feedback.hidden = false;
        retry.hidden = true;
        status.textContent = 'Importando…';
        setOpening(state, true);
        open.setAttribute('aria-busy', 'true');
        progress?.setNavigating(true);
        try {
            let result = await postImport(state, item, page);
            if (active !== state) return;
            if (!result) throw new Error('Título indisponível. Faça uma nova busca.');
            progress?.update(result.importProgress);
            if (!result.itemId) {
                status.textContent = item.isSeries
                    ? 'Importando a série. A ficha abre quando o Jellyfin reconhecer o título…'
                    : 'Filme importado. Aguardando a biblioteca do Jellyfin…';
            }

            for (let attempt = 0; active === state && !result.itemId && attempt < 40; attempt++) {
                await new Promise(resolve => setTimeout(resolve, 2000));
                if (active !== state) return;
                result = await request(state.client, item.isSeries ? 'WebSeries' : 'WebMovie',
                    { externalId: item.externalId });
                if (!result) throw new Error('A biblioteca Frame ficou indisponível.');
                progress?.update(result.importProgress);
                if (!result.itemId && result.importProgress?.status === 'failed')
                    throw new Error(result.importProgress.message || 'A importação da série falhou.');
            }
            if (active !== state) return;
            if (!result.itemId) throw new Error(item.isSeries
                ? 'A ficha da série ainda não apareceu no Jellyfin.'
                : 'Filme salvo. O Jellyfin ainda está atualizando a biblioteca.');

            status.textContent = 'Abrindo no Jellyfin…';
            const details = new URLSearchParams({ id: result.itemId });
            const serverId = state.client.api.serverId?.();
            if (serverId) details.set('serverId', serverId);
            const detailsHash = '#/details?' + details;
            location.hash = detailsHash;
            if (result.primaryImageReady === false)
                refreshDetailsWhenArtworkArrives(state.client, item, result.itemId, detailsHash);
        } catch (error) {
            if (active === state) {
                status.textContent = progress?.canRetry() ? '' : error.message;
                retry.hidden = progress?.canRetry() ?? false;
            }
        } finally {
            progress?.setNavigating(false);
            setOpening(state, false);
            open.removeAttribute('aria-busy');
        }
    }

    function syncSearch() {
        const field = document.querySelector('#searchPage .searchFields #searchTextInput');
        const term = field?.value.trim();
        const params = new URLSearchParams(location.hash.split('?')[1]);
        if (!field || !term || term.length < 2 || params.has('parentId')) {
            reset();
            clearFilter();
            return;
        }
        const client = requestClient();
        if (!client) {
            reset();
            clearFilter();
            return;
        }
        if (filter && filter.field !== field) clearFilter();
        const type = filter?.select.value ?? '';
        if (active?.field === field && active.term === term && active.type === type && active.client.token === client.token) {
            placeAfterLibrary(active);
            return;
        }

        reset();
        const state = {
            field, term, type, client,
            page: field.closest('#searchPage'),
            nextPage: 1,
            hasNext: true,
            seenTitles: new Set(),
            seenPages: new Set(),
            pendingPosters: new Map(),
            viewAbort: new AbortController(),
            ...createSection()
        };
        active = state;
        if (filter) showFilter(state);
        else placeAfterLibrary(state);
        state.queryTimer = setTimeout(() => loadNextPage(state), 500);
    }

    document.addEventListener('input', event => {
        if (event.target.id === 'searchTextInput' && !event.isComposing) syncSearch();
    });
    document.addEventListener('compositionend', event => {
        if (event.target.id === 'searchTextInput') syncSearch();
    });
    window.addEventListener('hashchange', syncSearch);
    new MutationObserver(syncSearch).observe(document.body, { childList: true, subtree: true });
    syncSearch();
})();