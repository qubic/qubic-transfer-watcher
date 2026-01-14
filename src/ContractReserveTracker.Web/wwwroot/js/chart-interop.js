// Chart.js interop for Blazor
let reserveChartInstance = null;
let reserveTimelineChartInstance = null;

window.renderReserveChart = function (canvasId, labels, burnData, deductData) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) {
        console.error('Canvas not found:', canvasId);
        return;
    }

    const ctx = canvas.getContext('2d');

    // Destroy existing chart if it exists
    if (reserveChartInstance) {
        reserveChartInstance.destroy();
    }

    // Qubic theme colors
    const qubicGreen = '#00ff88';
    const qubicRed = '#ff4466';
    const qubicCyan = '#00e5ff';
    const textColor = '#94a3b8';
    const gridColor = 'rgba(148, 163, 184, 0.1)';

    reserveChartInstance = new Chart(ctx, {
        type: 'bar',
        data: {
            labels: labels,
            datasets: [
                {
                    label: 'Burned (M)',
                    data: burnData,
                    backgroundColor: qubicGreen + '80',
                    borderColor: qubicGreen,
                    borderWidth: 1,
                    borderRadius: 4
                },
                {
                    label: 'Deducted (M)',
                    data: deductData,
                    backgroundColor: qubicRed + '80',
                    borderColor: qubicRed,
                    borderWidth: 1,
                    borderRadius: 4
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: {
                intersect: false,
                mode: 'index'
            },
            plugins: {
                legend: {
                    position: 'top',
                    labels: {
                        color: textColor,
                        usePointStyle: true,
                        padding: 20
                    }
                },
                tooltip: {
                    backgroundColor: '#1e293b',
                    titleColor: qubicCyan,
                    bodyColor: textColor,
                    borderColor: qubicCyan,
                    borderWidth: 1,
                    padding: 12,
                    displayColors: true,
                    callbacks: {
                        label: function(context) {
                            let value = context.parsed.y;
                            if (value >= 1000) {
                                return context.dataset.label + ': ' + (value / 1000).toFixed(2) + 'B';
                            }
                            return context.dataset.label + ': ' + value.toFixed(2) + 'M';
                        }
                    }
                }
            },
            scales: {
                x: {
                    grid: {
                        color: gridColor,
                        drawBorder: false
                    },
                    ticks: {
                        color: textColor
                    }
                },
                y: {
                    grid: {
                        color: gridColor,
                        drawBorder: false
                    },
                    ticks: {
                        color: textColor,
                        callback: function(value) {
                            if (value >= 1000) {
                                return (value / 1000).toFixed(1) + 'B';
                            }
                            return value + 'M';
                        }
                    },
                    beginAtZero: true
                }
            }
        }
    });
};

// Reserve timeline chart - shows reserve over time with each event as a point
window.renderReserveTimelineChart = function (canvasId, dataPoints) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) {
        console.error('Canvas not found:', canvasId);
        return;
    }

    if (!dataPoints || dataPoints.length === 0) {
        console.log('No data points for timeline chart');
        return;
    }

    const ctx = canvas.getContext('2d');

    // Destroy existing chart if it exists
    if (reserveTimelineChartInstance) {
        reserveTimelineChartInstance.destroy();
    }

    // Qubic theme colors
    const qubicGreen = '#00ff88';
    const qubicRed = '#ff4466';
    const qubicCyan = '#00e5ff';
    const textColor = '#94a3b8';
    const gridColor = 'rgba(148, 163, 184, 0.1)';

    // Prepare data - each point has timestamp, reserve, eventType, amount
    const chartData = dataPoints.map(p => ({
        x: new Date(p.timestamp),
        y: p.reserve,
        eventType: p.eventType,
        amount: p.amount,
        epoch: p.epoch
    }));

    // Point colors based on event type
    const pointColors = dataPoints.map(p => p.eventType === 'burn' ? qubicGreen : qubicRed);
    const pointBorderColors = dataPoints.map(p => p.eventType === 'burn' ? qubicGreen : qubicRed);

    reserveTimelineChartInstance = new Chart(ctx, {
        type: 'line',
        data: {
            datasets: [{
                label: 'Reserve',
                data: chartData,
                borderColor: qubicCyan,
                backgroundColor: qubicCyan + '20',
                borderWidth: 2,
                fill: true,
                tension: 0.1,
                pointRadius: 4,
                pointHoverRadius: 6,
                pointBackgroundColor: pointColors,
                pointBorderColor: pointBorderColors,
                pointBorderWidth: 2
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: {
                intersect: false,
                mode: 'nearest'
            },
            plugins: {
                legend: {
                    display: false
                },
                tooltip: {
                    backgroundColor: '#1e293b',
                    titleColor: qubicCyan,
                    bodyColor: textColor,
                    borderColor: qubicCyan,
                    borderWidth: 1,
                    padding: 12,
                    displayColors: false,
                    callbacks: {
                        title: function(context) {
                            const point = context[0].raw;
                            return new Date(point.x).toLocaleString();
                        },
                        label: function(context) {
                            const point = context.raw;
                            const lines = [];
                            lines.push('Reserve: ' + formatQubicAmount(point.y));
                            const sign = point.eventType === 'burn' ? '+' : '';
                            const eventLabel = point.eventType === 'burn' ? 'Burned' : 'Deducted';
                            lines.push(eventLabel + ': ' + sign + formatQubicAmount(point.amount));
                            lines.push('Epoch: ' + point.epoch);
                            return lines;
                        }
                    }
                }
            },
            scales: {
                x: {
                    type: 'time',
                    time: {
                        displayFormats: {
                            hour: 'MMM d, HH:mm',
                            day: 'MMM d',
                            week: 'MMM d'
                        }
                    },
                    grid: {
                        color: gridColor,
                        drawBorder: false
                    },
                    ticks: {
                        color: textColor,
                        maxTicksLimit: 8
                    }
                },
                y: {
                    grid: {
                        color: gridColor,
                        drawBorder: false
                    },
                    ticks: {
                        color: textColor,
                        callback: function(value) {
                            return formatQubicAmount(value);
                        }
                    },
                    beginAtZero: true
                }
            }
        }
    });
};

// Helper function to format Qubic amounts
function formatQubicAmount(amount) {
    const absAmount = Math.abs(amount);
    if (absAmount >= 1e12) {
        return (amount / 1e12).toFixed(2) + 'T';
    }
    if (absAmount >= 1e9) {
        return (amount / 1e9).toFixed(2) + 'B';
    }
    if (absAmount >= 1e6) {
        return (amount / 1e6).toFixed(2) + 'M';
    }
    if (absAmount >= 1e3) {
        return (amount / 1e3).toFixed(2) + 'K';
    }
    return amount.toLocaleString();
}
