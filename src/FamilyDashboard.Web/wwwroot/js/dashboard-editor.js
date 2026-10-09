// Layout editor for the /admin screen canvas.
//
// Moving and resizing run entirely in the browser: the block is snapped to the 12 x 12 grid while
// the pointer moves, and the final position is sent to the server once, on release. This keeps
// dragging smooth over the Blazor Server connection and works for mouse, pen and touch alike.
window.dashboardEditor = (() => {
    const GridSize = 12;
    const DragThreshold = 4;
    let dotNet = null;
    let active = null;
    let tooltip = null;

    const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

    function readLayout(block) {
        return {
            x: Number(block.dataset.x),
            y: Number(block.dataset.y),
            w: Number(block.dataset.w),
            h: Number(block.dataset.h)
        };
    }

    // Distance between the starts of two neighbouring cells, measured from the live canvas so it
    // stays right at any window size or aspect ratio.
    function gridStep(canvas) {
        const styles = getComputedStyle(canvas);
        const columnGap = Number.parseFloat(styles.columnGap) || 0;
        const rowGap = Number.parseFloat(styles.rowGap) || 0;
        const width = canvas.clientWidth - Number.parseFloat(styles.paddingLeft) - Number.parseFloat(styles.paddingRight);
        const height = canvas.clientHeight - Number.parseFloat(styles.paddingTop) - Number.parseFloat(styles.paddingBottom);
        return { x: (width + columnGap) / GridSize, y: (height + rowGap) / GridSize };
    }

    // Applies a layout to the block. `edges` lists which edges a resize moves; null means a move.
    function layoutFor(start, dx, dy, edges) {
        if (!edges) {
            return {
                x: clamp(start.x + dx, 1, GridSize + 1 - start.w),
                y: clamp(start.y + dy, 1, GridSize + 1 - start.h),
                w: start.w,
                h: start.h
            };
        }

        let left = start.x;
        let top = start.y;
        let right = start.x + start.w - 1;
        let bottom = start.y + start.h - 1;
        if (edges.includes('left')) left = clamp(start.x + dx, 1, right);
        if (edges.includes('right')) right = clamp(right + dx, left, GridSize);
        if (edges.includes('top')) top = clamp(start.y + dy, 1, bottom);
        if (edges.includes('bottom')) bottom = clamp(bottom + dy, top, GridSize);
        return { x: left, y: top, w: right - left + 1, h: bottom - top + 1 };
    }

    function render(block, layout) {
        block.style.gridColumn = `${layout.x} / span ${layout.w}`;
        block.style.gridRow = `${layout.y} / span ${layout.h}`;
    }

    function showTooltip(event, layout) {
        if (!tooltip) {
            tooltip = document.createElement('div');
            tooltip.className = 'canvas-layout-tooltip';
            document.body.appendChild(tooltip);
        }
        tooltip.textContent = `Column ${layout.x}, row ${layout.y} · ${layout.w} × ${layout.h}`;
        tooltip.style.left = `${event.clientX + 14}px`;
        tooltip.style.top = `${event.clientY + 14}px`;
        tooltip.hidden = false;
    }

    function hideTooltip() {
        if (tooltip) tooltip.hidden = true;
    }

    function edgesFor(handle) {
        const direction = [...handle.classList].find(name => name.startsWith('resize-'))?.slice('resize-'.length) ?? '';
        return ['top', 'bottom', 'left', 'right'].filter(edge => direction.includes(edge));
    }

    // Saves a layout on the server. If the save fails, the block snaps back to where it was.
    function commit(block, layout, originalStyle) {
        const start = readLayout(block);
        if (layout.x === start.x && layout.y === start.y && layout.w === start.w && layout.h === start.h) {
            return;
        }
        if (!dotNet) {
            block.setAttribute('style', originalStyle);
            return;
        }
        dotNet.invokeMethodAsync('CommitWidgetLayout', Number(block.dataset.widgetId), layout.x, layout.y, layout.w, layout.h)
            .catch(() => block.setAttribute('style', originalStyle));
    }

    function onPointerDown(event) {
        if (event.button !== 0 || active) return;
        const block = event.target.closest('.screen-canvas .canvas-block');
        if (!block || event.target.closest('button')) return;

        const handle = event.target.closest('.widget-resize-handle');
        const canvas = block.closest('.screen-canvas');
        active = {
            block,
            canvas,
            pointerId: event.pointerId,
            startX: event.clientX,
            startY: event.clientY,
            start: readLayout(block),
            step: gridStep(canvas),
            edges: handle ? edgesFor(handle) : null,
            originalStyle: block.getAttribute('style'),
            started: !!handle,
            layout: null
        };
        block.setPointerCapture(event.pointerId);
        if (handle) {
            event.preventDefault();
            block.classList.add('is-resizing');
        }
    }

    function onPointerMove(event) {
        if (!active || event.pointerId !== active.pointerId) return;
        const offsetX = event.clientX - active.startX;
        const offsetY = event.clientY - active.startY;
        if (!active.started) {
            if (Math.hypot(offsetX, offsetY) < DragThreshold) return;
            active.started = true;
            active.block.classList.add('is-moving');
        }

        const dx = Math.round(offsetX / active.step.x);
        const dy = Math.round(offsetY / active.step.y);
        active.layout = layoutFor(active.start, dx, dy, active.edges);
        render(active.block, active.layout);
        showTooltip(event, active.layout);
    }

    function finish(event, keep) {
        if (!active || event.pointerId !== active.pointerId) return;
        const { block, layout, started, originalStyle } = active;
        block.classList.remove('is-moving', 'is-resizing');
        hideTooltip();
        if (block.hasPointerCapture(event.pointerId)) block.releasePointerCapture(event.pointerId);

        if (started && keep && layout) {
            commit(block, layout, originalStyle);
        } else if (started) {
            block.setAttribute('style', originalStyle);
        }
        active = null;
    }

    function onKeyDown(event) {
        if (event.key === 'Escape' && active) {
            active.block.setAttribute('style', active.originalStyle);
            active.block.classList.remove('is-moving', 'is-resizing');
            hideTooltip();
            active = null;
            return;
        }

        const block = event.target.closest?.('.screen-canvas .canvas-block');
        const delta = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key];
        if (!block || !delta || event.target !== block) return;

        event.preventDefault();
        const start = readLayout(block);
        // Arrow keys move the block; Shift + arrow keys grow or shrink it from the bottom-right corner.
        const layout = event.shiftKey
            ? layoutFor(start, delta[0], delta[1], ['right', 'bottom'])
            : layoutFor(start, delta[0], delta[1], null);
        const originalStyle = block.getAttribute('style');
        render(block, layout);
        commit(block, layout, originalStyle);
    }

    const onPointerUp = event => finish(event, true);
    const onPointerCancel = event => finish(event, false);
    const listeners = [
        ['pointerdown', onPointerDown],
        ['pointermove', onPointerMove],
        ['pointerup', onPointerUp],
        ['pointercancel', onPointerCancel],
        ['keydown', onKeyDown]
    ];

    function attach(dotNetReference) {
        if (!dotNet) {
            listeners.forEach(([type, listener]) => document.addEventListener(type, listener));
        }
        dotNet = dotNetReference;
    }

    function detach() {
        listeners.forEach(([type, listener]) => document.removeEventListener(type, listener));
        dotNet = null;
        active = null;
        tooltip?.remove();
        tooltip = null;
    }

    return { attach, detach };
})();
