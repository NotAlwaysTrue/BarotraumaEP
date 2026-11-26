export const moduleNames = Object.freeze({
    Update: 'Total update',
    GameSession: 'Game session',
    Level: 'Level',
    Characters: 'Characters',
    StatusEffects: 'Status effects',
    MapEntities: 'Map entities',
    AnimationAndRagdolls: 'Animation and ragdolls',
    Submarines: 'Submarines',
    Physics: 'Physics',
    Networking: 'Server and network'
});

export const historySeconds = 300;
export const chartMetrics = {
    update: { label: 'Update time', unit: 'ms', series: [
        { key: 'averageUpdateMs', label: 'Update time', color: 'primary' }
    ] },
    tps: { label: 'Simulation TPS', unit: 'TPS', series: [
        { key: 'updatesPerSecond', label: 'Simulation TPS', color: 'primary' },
        { key: 'targetUpdateRate', label: 'Target TPS', color: 'mean', dash: [5, 4] }
    ] },
    cpu: { label: 'CPU usage', unit: '%', series: [{ key: 'cpuPercent', label: 'Process CPU', color: 'primary' }] },
    memory: { label: 'Process memory', unit: 'MiB', series: [{ key: 'workingSetBytes', label: 'Working set', color: 'primary', divisor: 1048576 }] },
    heap: { label: 'Managed heap', unit: 'MiB', series: [{ key: 'managedHeapBytes', label: 'Managed heap', color: 'mean', divisor: 1048576 }] }
};

for (const [module, label] of Object.entries(moduleNames))
{
    if (module === 'Update') { continue; }
    const timing = sample => sample.modules?.find(entry => entry.module === module);
    chartMetrics['module:' + module] = { label, unit: 'ms', series: [
        { read: sample => timing(sample)?.averageMilliseconds, label, color: 'primary' }
    ] };
}

export function combineChartMetrics(primary, compared)
{
    const { unit } = chartMetrics[primary];
    const compatible = Object.keys(chartMetrics).filter(key => chartMetrics[key].unit === unit);
    const selected = compatible.filter(key => key !== primary && compared.has(key));
    if (!selected.length) { return chartMetrics[primary]; }

    const series = [primary, ...selected].flatMap(key => {
        const metric = chartMetrics[key];
        const color = 'series-' + (compatible.indexOf(key) + 1);
        return metric.series.map((line, index) => ({
            ...line,
            label: metric.series.length === 1 ? line.label : metric.label + ' / ' + line.label,
            color,
            dash: index ? [5, 4] : []
        }));
    });
    return { unit, label: [primary, ...selected].map(key => chartMetrics[key].label).join(', '), series };
}

export function classifyTps(value)
{
    if (!Number.isFinite(value))
    {
        return 'unavailable';
    }
    if (value < 50)
    {
        return 'critical';
    }
    if (value < 55)
    {
        return 'warning';
    }
    return 'normal';
}

export function drawHistory(canvas, samples, metric, chartWindowSeconds)
{
    const width = canvas.clientWidth;
    const height = canvas.clientHeight;
    if (!width || !height) { return; }
    const ratio = window.devicePixelRatio || 1;
    const pixelWidth = Math.round(width * ratio);
    const pixelHeight = Math.round(height * ratio);
    if (canvas.width !== pixelWidth || canvas.height !== pixelHeight)
    {
        canvas.width = pixelWidth;
        canvas.height = pixelHeight;
    }

    const context = canvas.getContext('2d');
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.clearRect(0, 0, width, height);
    const styles = getComputedStyle(canvas);
    const color = name => styles.getPropertyValue('--' + name).trim();
    context.font = '12px Arial, sans-serif';
    context.fillStyle = color('muted');
    if (!metric)
    {
        context.textAlign = 'center';
        context.fillText('Select data to show on the chart', width / 2, height / 2);
        return;
    }
    const end = samples.at(-1)?.uptimeSeconds ?? 0;
    const visibleSamples = samples.filter(sample => sample.uptimeSeconds >= end - chartWindowSeconds);
    const left = 61;
    const right = width - 16;
    const top = 16;
    const bottom = height - 26;
    const x = time => right - (end - time) / chartWindowSeconds * (right - left);
    let maxValue = 0;
    let hasValue = false;
    const valueFor = (sample, series) => series.read ? series.read(sample) : sample[series.key];
    for (const series of metric.series)
    {
        for (const sample of visibleSamples)
        {
            const value = valueFor(sample, series);
            if (!Number.isFinite(value)) { continue; }
            maxValue = Math.max(maxValue, value / (series.divisor ?? 1));
            hasValue = true;
        }
    }

    if (!hasValue)
    {
        context.textAlign = 'center';
        context.fillText('Waiting for samples', width / 2, height / 2);
        return;
    }
    const unit = Math.pow(10, Math.floor(Math.log10(Math.max(maxValue, 1) / 4)));
    const step = ([1, 2, 5, 10].find(scale => scale * unit >= Math.max(maxValue, 1) / 4) ?? 10) * unit;
    const ticks = Math.ceil(Math.max(maxValue, step) / step);
    const y = value => bottom - value / (ticks * step) * (bottom - top);

    context.lineWidth = 1;
    context.setLineDash([]);
    context.strokeStyle = color('chart-track');
    context.textAlign = 'right';
    for (let tick = 0; tick <= ticks; tick++)
    {
        const value = tick * step;
        const row = y(value);
        context.beginPath();
        context.moveTo(left, row);
        context.lineTo(right, row);
        context.stroke();
        context.fillText(Number(value.toPrecision(5)) + ' ' + metric.unit, left - 8, row + 4);
    }
    context.textAlign = 'center';
    const intervals = width < 420 ? [1, 0.5, 0] : [1, 0.75, 0.5, 0.25, 0];
    for (const fraction of intervals)
    {
        const offset = fraction * chartWindowSeconds;
        const roundedOffset = Math.round(offset);
        const minutes = Math.floor(roundedOffset / 60);
        const seconds = roundedOffset % 60;
        const elapsed = [minutes ? minutes + 'm' : '', seconds ? seconds + 's' : ''].filter(Boolean).join(' ');
        const label = offset === 0 ? 'Now' : elapsed + ' ago';
        context.fillText(label, Math.min(right - 4, x(end - offset)), height - 5);
    }

    context.save();
    context.beginPath();
    context.rect(left - 3, top - 3, right - left + 6, bottom - top + 6);
    context.clip();
    context.lineWidth = 2;
    for (const series of metric.series)
    {
        const { color: seriesColor, dash = [], divisor = 1 } = series;
        let previousTime = null;
        let point = null;
        context.beginPath();
        context.strokeStyle = color(seriesColor);
        context.setLineDash(dash);
        for (const sample of visibleSamples)
        {
            const value = valueFor(sample, series);
            if (!Number.isFinite(value))
            {
                previousTime = null;
                continue;
            }
            const time = sample.uptimeSeconds;
            point = { x: x(time), y: y(value / divisor) };
            if (previousTime === null || time - previousTime > 3) { context.moveTo(point.x, point.y); }
            else { context.lineTo(point.x, point.y); }
            previousTime = time;
        }
        context.stroke();
        if (point)
        {
            context.beginPath();
            context.fillStyle = color(seriesColor);
            context.arc(point.x, point.y, 3, 0, 2 * Math.PI);
            context.fill();
        }
    }
    context.restore();
}
