// Package agent связывает сборщики и транспорт: по расписанию собирает
// метрики и инвентарь и отправляет их в центр.
package agent

import (
	"context"
	"log/slog"
	"runtime"
	"sync"
	"time"

	"github.com/shirou/gopsutil/v4/process"

	"icc/agent/internal/collect"
	"icc/agent/internal/enroll"
	agentv1 "icc/agent/internal/pb/icc/agent/v1"
	"icc/agent/internal/transport"
)

type Agent struct {
	identity *enroll.Identity
	docker   *collect.Docker
	client   *transport.Client
	log      *slog.Logger

	mu     sync.Mutex
	config *agentv1.AgentConfig
	reload chan struct{}
	host   *agentv1.HostInfo
}

func New(center, version string, identity *enroll.Identity, docker *collect.Docker,
	host *agentv1.HostInfo, log *slog.Logger) *Agent {
	a := &Agent{
		identity: identity, docker: docker, log: log, host: host,
		config: &agentv1.AgentConfig{HeartbeatIntervalSeconds: 10, MetricsIntervalSeconds: 5, InventoryIntervalSeconds: 30},
		reload: make(chan struct{}, 1),
	}
	a.client = transport.New(center, identity, version, a.hostInfo, a.applyConfig, log)
	return a
}

func (a *Agent) hostInfo() *agentv1.HostInfo {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.host
}

func (a *Agent) applyConfig(c *agentv1.AgentConfig) {
	if c == nil {
		return
	}
	a.mu.Lock()
	a.config = c
	a.mu.Unlock()
	select {
	case a.reload <- struct{}{}:
	default:
	}
}

func interval(seconds uint32, fallback time.Duration) time.Duration {
	if seconds == 0 {
		return fallback
	}
	return time.Duration(seconds) * time.Second
}

func (a *Agent) Run(ctx context.Context) {
	go a.client.Run(ctx)

	self, _ := process.NewProcessWithContext(ctx, int32(osPid()))
	for ctx.Err() == nil {
		a.mu.Lock()
		cfg := a.config
		a.mu.Unlock()
		heartbeat := time.NewTicker(interval(cfg.GetHeartbeatIntervalSeconds(), 10*time.Second))
		metrics := time.NewTicker(interval(cfg.GetMetricsIntervalSeconds(), 5*time.Second))
		inventory := time.NewTicker(interval(cfg.GetInventoryIntervalSeconds(), 30*time.Second))
		a.sendInventory(ctx)

	loop:
		for {
			select {
			case <-ctx.Done():
				break loop
			case <-a.reload:
				break loop // новые интервалы от центра
			case <-heartbeat.C:
				a.sendHeartbeat(ctx, self)
			case <-metrics.C:
				a.sendMetrics(ctx)
			case <-inventory.C:
				a.sendInventory(ctx)
			}
		}
		heartbeat.Stop()
		metrics.Stop()
		inventory.Stop()
	}
}

func (a *Agent) sendHeartbeat(ctx context.Context, self *process.Process) {
	hb := &agentv1.Heartbeat{BufferedMessages: uint64(a.client.Buffered())}
	if self != nil {
		if pct, err := self.CPUPercentWithContext(ctx); err == nil {
			hb.AgentCpuPercent = pct
		}
		if mem, err := self.MemoryInfoWithContext(ctx); err == nil {
			hb.AgentRssBytes = mem.RSS
		}
	}
	a.client.Send(&agentv1.ConnectRequest{Payload: &agentv1.ConnectRequest_Heartbeat{Heartbeat: hb}})
}

func (a *Agent) sendMetrics(ctx context.Context) {
	ctx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	hostname := a.hostInfo().GetHostname()
	samples := collect.HostMetrics(ctx, hostname)
	containers, err := a.docker.ContainerMetrics(ctx, hostname)
	if err != nil {
		a.log.Warn("container metrics failed", "error", err)
	}
	samples = append(samples, containers...)
	samples = append(samples, a.selfMetrics(hostname)...)
	a.client.Send(&agentv1.ConnectRequest{Payload: &agentv1.ConnectRequest_Metrics{
		Metrics: &agentv1.MetricBatch{Samples: samples},
	}})
}

func (a *Agent) selfMetrics(hostname string) []*agentv1.MetricSample {
	var ms runtime.MemStats
	runtime.ReadMemStats(&ms)
	labels := map[string]string{"host": hostname}
	return []*agentv1.MetricSample{
		{Name: "icc_agent_queue_messages", Labels: labels, Value: float64(a.client.Buffered())},
		{Name: "icc_agent_dropped_messages_total", Labels: labels, Value: float64(a.client.Dropped())},
		{Name: "icc_agent_heap_bytes", Labels: labels, Value: float64(ms.HeapAlloc)},
	}
}

func (a *Agent) sendInventory(ctx context.Context) {
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	host := collect.HostInfo(ctx, a.docker.Version(ctx))
	a.mu.Lock()
	a.host = host
	a.mu.Unlock()
	snap, err := a.docker.Inventory(ctx, host)
	if err != nil {
		a.log.Warn("inventory failed", "error", err)
		return
	}
	a.client.Send(&agentv1.ConnectRequest{Payload: &agentv1.ConnectRequest_Inventory{Inventory: snap}})
}
