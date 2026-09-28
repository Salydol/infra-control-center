package collect

import (
	"math"
	"testing"
	"time"

	"github.com/moby/moby/api/types/container"
)

func TestCPUCores(t *testing.T) {
	t0 := time.Unix(1000, 0)
	prev := cpuSample{total: 1_000_000_000, read: t0}
	cur := cpuSample{total: 2_500_000_000, read: t0.Add(2 * time.Second)}

	cores, ok := CPUCores(prev, cur, true)
	if !ok || math.Abs(cores-0.75) > 1e-9 {
		t.Fatalf("cores=%v ok=%v, want 0.75", cores, ok)
	}
	if _, ok := CPUCores(prev, cur, false); ok {
		t.Error("no previous sample must not produce a value")
	}
	if _, ok := CPUCores(cur, prev, true); ok {
		t.Error("counter reset (container restart) must not produce a value")
	}
}

func TestMemoryWorkingSet(t *testing.T) {
	v2 := container.MemoryStats{Usage: 100, Stats: map[string]uint64{"inactive_file": 30}}
	if got := MemoryWorkingSet(v2); got != 70 {
		t.Errorf("cgroup v2: got %d, want 70", got)
	}
	v1 := container.MemoryStats{Usage: 100, Stats: map[string]uint64{"total_inactive_file": 40, "inactive_file": 1}}
	if got := MemoryWorkingSet(v1); got != 60 {
		t.Errorf("cgroup v1: got %d, want 60", got)
	}
	if got := MemoryWorkingSet(container.MemoryStats{Usage: 10, Stats: map[string]uint64{"inactive_file": 50}}); got != 0 {
		t.Errorf("underflow: got %d, want 0", got)
	}
}

func TestIgnoreMatch(t *testing.T) {
	ig := DefaultIgnore()
	cases := []struct {
		image  string
		labels map[string]string
		want   bool
	}{
		{"shop-api:1.0.0", map[string]string{"com.docker.compose.service": "api"}, false},
		{"gaiaadm/pumba:1.2.1", nil, true},
		{"ghcr.io/alexei-led/stress-ng:latest", nil, true},
		{"busybox", map[string]string{"icc.fault-injector": "true"}, true},
	}
	for _, c := range cases {
		if got := ig.Match(c.image, c.labels); got != c.want {
			t.Errorf("Match(%q, %v) = %v, want %v", c.image, c.labels, got, c.want)
		}
	}
}

func TestEnvHashIgnoresOrder(t *testing.T) {
	a := EnvHash([]string{"A=1", "B=2"})
	if a != EnvHash([]string{"B=2", "A=1"}) {
		t.Error("hash must not depend on variable order")
	}
	if a == EnvHash([]string{"A=1", "B=3"}) {
		t.Error("hash must change when a value changes")
	}
}
