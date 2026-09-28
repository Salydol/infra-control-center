package collect

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"maps"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/moby/moby/api/types/container"
	"github.com/moby/moby/client"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "icc/agent/internal/pb/icc/agent/v1"
)

const (
	labelProject     = "com.docker.compose.project"
	labelService     = "com.docker.compose.service"
	labelWorkingDir  = "com.docker.compose.project.working_dir"
	labelConfigFiles = "com.docker.compose.project.config_files"
	labelEnvFiles    = "com.docker.compose.project.environment_file"
)

// Ignore — какие контейнеры агент не видит: вспомогательные контейнеры
// инжектора сбоев стенда, иначе модель находила бы причину по их появлению.
type Ignore struct {
	Labels        map[string]string
	ImagePrefixes []string
}

func DefaultIgnore() Ignore {
	return Ignore{
		Labels: map[string]string{"icc.fault-injector": "true"},
		ImagePrefixes: []string{
			"gaiaadm/pumba", "ghcr.io/alexei-led/pumba", "ghcr.io/alexei-led/stress-ng", "alexeiled/stress-ng",
		},
	}
}

func (ig Ignore) Match(image string, labels map[string]string) bool {
	for k, v := range ig.Labels {
		if labels[k] == v {
			return true
		}
	}
	for _, p := range ig.ImagePrefixes {
		if strings.HasPrefix(image, p) {
			return true
		}
	}
	return false
}

type cpuSample struct {
	total    uint64
	read     time.Time
	throttle uint64
}

type Docker struct {
	cli    *client.Client
	ignore Ignore

	mu      sync.Mutex
	prevCPU map[string]cpuSample
}

func NewDocker(ignore Ignore) (*Docker, error) {
	cli, err := client.New(client.FromEnv, client.WithAPIVersionNegotiation())
	if err != nil {
		return nil, err
	}
	return &Docker{cli: cli, ignore: ignore, prevCPU: map[string]cpuSample{}}, nil
}

func (d *Docker) Close() error { return d.cli.Close() }

func (d *Docker) Version(ctx context.Context) string {
	v, err := d.cli.ServerVersion(ctx, client.ServerVersionOptions{})
	if err != nil {
		return ""
	}
	return v.Version
}

func (d *Docker) list(ctx context.Context, all bool) ([]container.Summary, error) {
	res, err := d.cli.ContainerList(ctx, client.ContainerListOptions{All: all})
	if err != nil {
		return nil, err
	}
	return slices.DeleteFunc(res.Items, func(c container.Summary) bool { return d.ignore.Match(c.Image, c.Labels) }), nil
}

// Inventory — полный снимок контейнеров и compose-проектов хоста.
func (d *Docker) Inventory(ctx context.Context, host *agentv1.HostInfo) (*agentv1.InventorySnapshot, error) {
	items, err := d.list(ctx, true)
	if err != nil {
		return nil, err
	}
	snap := &agentv1.InventorySnapshot{CollectedAt: timestamppb.Now(), Host: host}
	projects := map[string]*agentv1.ComposeProject{}

	for _, s := range items {
		ins, err := d.cli.ContainerInspect(ctx, s.ID, client.ContainerInspectOptions{})
		if err != nil {
			continue // контейнер мог исчезнуть между list и inspect
		}
		snap.Containers = append(snap.Containers, toContainer(s, ins.Container))

		if name := s.Labels[labelProject]; name != "" {
			p := projects[name]
			if p == nil {
				p = &agentv1.ComposeProject{
					Name:        name,
					WorkingDir:  s.Labels[labelWorkingDir],
					ConfigFiles: splitList(s.Labels[labelConfigFiles]),
					EnvFiles:    splitList(s.Labels[labelEnvFiles]),
				}
				projects[name] = p
			}
			if svc := s.Labels[labelService]; svc != "" && !slices.Contains(p.Services, svc) {
				p.Services = append(p.Services, svc)
			}
		}
	}
	for _, name := range slices.Sorted(maps.Keys(projects)) {
		p := projects[name]
		slices.Sort(p.Services)
		snap.ComposeProjects = append(snap.ComposeProjects, p)
	}
	return snap, nil
}

func toContainer(s container.Summary, ins container.InspectResponse) *agentv1.Container {
	c := &agentv1.Container{
		Id:             s.ID,
		Name:           containerName(s.Names),
		Image:          s.Image,
		ImageId:        ins.Image,
		State:          string(s.State),
		Status:         s.Status,
		CreatedAt:      timestamppb.New(time.Unix(s.Created, 0)),
		RestartCount:   uint32(ins.RestartCount),
		ComposeProject: s.Labels[labelProject],
		ComposeService: s.Labels[labelService],
		Labels:         s.Labels,
	}
	if ins.State != nil {
		if t, err := time.Parse(time.RFC3339Nano, ins.State.StartedAt); err == nil && !t.IsZero() {
			c.StartedAt = timestamppb.New(t)
		}
	}
	if ins.Config != nil {
		c.EnvHash = EnvHash(ins.Config.Env)
		if ins.Config.Image != "" {
			c.Image = ins.Config.Image
		}
	}
	if ins.HostConfig != nil {
		c.Limits = &agentv1.ResourceLimits{Cpus: float64(ins.HostConfig.NanoCPUs) / 1e9, MemoryBytes: uint64(max(ins.HostConfig.Memory, 0))}
	}
	for _, p := range s.Ports {
		pb := &agentv1.PortBinding{ContainerPort: uint32(p.PrivatePort), Protocol: p.Type, HostPort: uint32(p.PublicPort)}
		if p.IP.IsValid() {
			pb.HostIp = p.IP.String()
		}
		c.Ports = append(c.Ports, pb)
	}
	return c
}

// EnvHash — хеш итогового окружения контейнера без учёта порядка переменных.
func EnvHash(env []string) string {
	sorted := slices.Clone(env)
	slices.Sort(sorted)
	h := sha256.Sum256([]byte(strings.Join(sorted, "\n")))
	return hex.EncodeToString(h[:])
}

// ContainerMetrics — ресурсы и состояние работающих контейнеров.
func (d *Docker) ContainerMetrics(ctx context.Context, hostname string) ([]*agentv1.MetricSample, error) {
	items, err := d.list(ctx, true)
	if err != nil {
		return nil, err
	}

	var (
		mu      sync.Mutex
		samples []*agentv1.MetricSample
		wg      sync.WaitGroup
		sem     = make(chan struct{}, 4)
		alive   = map[string]bool{}
	)
	for _, s := range items {
		alive[s.ID] = true
		wg.Add(1)
		go func() {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()
			out := d.containerSamples(ctx, hostname, s)
			mu.Lock()
			samples = append(samples, out...)
			mu.Unlock()
		}()
	}
	wg.Wait()

	d.mu.Lock()
	for id := range d.prevCPU {
		if !alive[id] {
			delete(d.prevCPU, id)
		}
	}
	d.mu.Unlock()
	return samples, nil
}

func (d *Docker) containerSamples(ctx context.Context, hostname string, s container.Summary) []*agentv1.MetricSample {
	b := newBatch(time.Now(), map[string]string{
		"host":            hostname,
		"container":       containerName(s.Names),
		"container_id":    shortID(s.ID),
		"compose_project": s.Labels[labelProject],
		"compose_service": s.Labels[labelService],
		"image":           s.Image,
	})

	running := s.State == container.StateRunning
	b.add("container_up", boolValue(running), nil)
	b.add("container_health", healthValue(s), nil)

	if ins, err := d.cli.ContainerInspect(ctx, s.ID, client.ContainerInspectOptions{}); err == nil {
		b.add("container_restarts", float64(ins.Container.RestartCount), nil)
		if ins.Container.State != nil {
			b.add("container_oom_killed", boolValue(ins.Container.State.OOMKilled), nil)
		}
	}
	if !running {
		return b.samples
	}

	res, err := d.cli.ContainerStats(ctx, s.ID, client.ContainerStatsOptions{Stream: false})
	if err != nil {
		return b.samples
	}
	defer res.Body.Close()
	var st container.StatsResponse
	if err := json.NewDecoder(res.Body).Decode(&st); err != nil {
		return b.samples
	}

	d.mu.Lock()
	prev, ok := d.prevCPU[s.ID]
	cur := cpuSample{total: st.CPUStats.CPUUsage.TotalUsage, read: st.Read, throttle: st.CPUStats.ThrottlingData.ThrottledTime}
	d.prevCPU[s.ID] = cur
	d.mu.Unlock()
	if cores, ok := CPUCores(prev, cur, ok); ok {
		b.add("container_cpu_usage_cores", cores, nil)
	}
	b.add("container_cpu_throttled_seconds_total", float64(st.CPUStats.ThrottlingData.ThrottledTime)/1e9, nil)

	b.add("container_memory_usage_bytes", float64(MemoryWorkingSet(st.MemoryStats)), nil)
	if st.MemoryStats.Limit > 0 {
		b.add("container_memory_limit_bytes", float64(st.MemoryStats.Limit), nil)
	}
	b.add("container_pids", float64(st.PidsStats.Current), nil)

	var rx, tx, rxErr, txErr, rxDrop, txDrop uint64
	for _, n := range st.Networks {
		rx += n.RxBytes
		tx += n.TxBytes
		rxErr += n.RxErrors
		txErr += n.TxErrors
		rxDrop += n.RxDropped
		txDrop += n.TxDropped
	}
	b.add("container_network_receive_bytes_total", float64(rx), nil)
	b.add("container_network_transmit_bytes_total", float64(tx), nil)
	b.add("container_network_receive_errors_total", float64(rxErr), nil)
	b.add("container_network_transmit_errors_total", float64(txErr), nil)
	b.add("container_network_receive_drop_total", float64(rxDrop), nil)
	b.add("container_network_transmit_drop_total", float64(txDrop), nil)

	var read, write uint64
	for _, e := range st.BlkioStats.IoServiceBytesRecursive {
		switch strings.ToLower(e.Op) {
		case "read":
			read += e.Value
		case "write":
			write += e.Value
		}
	}
	b.add("container_fs_read_bytes_total", float64(read), nil)
	b.add("container_fs_write_bytes_total", float64(write), nil)
	return b.samples
}

// CPUCores — сколько ядер контейнер использовал между двумя замерами.
func CPUCores(prev, cur cpuSample, havePrev bool) (float64, bool) {
	if !havePrev || cur.total < prev.total {
		return 0, false
	}
	elapsed := cur.read.Sub(prev.read)
	if elapsed <= 0 {
		return 0, false
	}
	return float64(cur.total-prev.total) / float64(elapsed.Nanoseconds()), true
}

// MemoryWorkingSet — память без вытесняемого файлового кэша (как в cAdvisor и docker stats).
func MemoryWorkingSet(m container.MemoryStats) uint64 {
	inactive := m.Stats["inactive_file"]             // cgroup v2
	if v, ok := m.Stats["total_inactive_file"]; ok { // cgroup v1
		inactive = v
	}
	if inactive > m.Usage {
		return 0
	}
	return m.Usage - inactive
}

func healthValue(s container.Summary) float64 {
	if s.Health == nil {
		return -1 // healthcheck не настроен
	}
	switch s.Health.Status {
	case container.Healthy:
		return 1
	case container.Starting:
		return 0.5
	default:
		return 0
	}
}

func containerName(names []string) string {
	if len(names) == 0 {
		return ""
	}
	return strings.TrimPrefix(names[0], "/")
}

func shortID(id string) string {
	if len(id) > 12 {
		return id[:12]
	}
	return id
}

func boolValue(b bool) float64 {
	if b {
		return 1
	}
	return 0
}

func splitList(s string) []string {
	if s == "" {
		return nil
	}
	parts := strings.Split(s, ",")
	for i := range parts {
		parts[i] = strings.TrimSpace(parts[i])
	}
	return parts
}
