// Chart.js interop for Blazor
let reserveChartInstance = null;

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
