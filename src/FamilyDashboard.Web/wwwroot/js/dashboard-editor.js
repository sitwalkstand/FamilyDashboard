window.captureDashboardPointer = (element, pointerId) => {
    element?.setPointerCapture(pointerId);
};

window.getDashboardGridMetrics = (element) => {
    const cell = element?.querySelector('.canvas-drop-cell');
    if (!cell) {
        return [40, 40];
    }

    const cellRect = cell.getBoundingClientRect();
    const styles = getComputedStyle(element);
    const columnGap = Number.parseFloat(styles.columnGap) || 0;
    const rowGap = Number.parseFloat(styles.rowGap) || 0;
    return [cellRect.width + columnGap, cellRect.height + rowGap];
};
