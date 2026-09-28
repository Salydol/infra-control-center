package collect

import (
	"context"
	"net"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/shirou/gopsutil/v4/cpu"
	"github.com/shirou/gopsutil/v4/disk"
	"github.com/shirou/gopsutil/v4/host"
	"github.com/shirou/gopsutil/v4/load"
	"github.com/shirou/gopsutil/v4/mem"
	psnet "github.com/shirou/gopsutil/v4/net"

	agentv1 "icc/agent/internal/pb/icc/agent/v1"
)

// Внутри контейнера агента хост виден через HOST_PROC/HOST_SYS (gopsutil читает их сам).

// HostInfo собирает описание хоста.
func HostInfo(ctx context.Context, dockerVersion string) *agentv1.HostInfo {
	info := &agentv1.HostInfo{DockerVersion: dockerVersion}
	if h, err := host.InfoWithContext(ctx); err == nil {
		info.Hostname = h.Hostname
		info.Os = strings.TrimSpace(h.Platform + " " + h.PlatformVersion)
		info.KernelVersion = h.KernelVersion
		info.Architecture = h.KernelArch
	}
	if n, err := cpu.CountsWithContext(ctx, true); err == nil {
		info.CpuCores = uint32(n)
	}
	if m, err := mem.VirtualMemoryWithContext(ctx); err == nil {
		info.MemoryTotalBytes = m.Total
	}
	if ifaces, err := net.Interfaces(); err == nil {
		for _, iface := range ifaces {
			if iface.Flags&net.FlagLoopback != 0 || iface.Flags&net.FlagUp == 0 || isVirtualInterface(iface.Name) {
				continue
			}
			addrs, _ := iface.Addrs()
			for _, a := range addrs {
				if ipnet, ok := a.(*net.IPNet); ok && ipnet.IP.To4() != nil {
					info.IpAddresses = append(info.IpAddresses, ipnet.IP.String())
				}
			}
		}
	}
	return info
}

// HostMetrics — загрузка CPU, память, диски, сеть хоста.
func HostMetrics(ctx context.Context, hostname string) []*agentv1.MetricSample {
	now := time.Now()
	b := newBatch(now, map[string]string{"host": hostname})

	if pct, err := cpu.PercentWithContext(ctx, 0, false); err == nil && len(pct) == 1 {
		b.add("host_cpu_usage_ratio", pct[0]/100, nil)
	}
	if l, err := load.AvgWithContext(ctx); err == nil {
		b.add("host_load1", l.Load1, nil)
		b.add("host_load5", l.Load5, nil)
		b.add("host_load15", l.Load15, nil)
	}
	if m, err := mem.VirtualMemoryWithContext(ctx); err == nil {
		b.add("host_memory_total_bytes", float64(m.Total), nil)
		b.add("host_memory_used_bytes", float64(m.Used), nil)
		b.add("host_memory_available_bytes", float64(m.Available), nil)
	}
	if s, err := mem.SwapMemoryWithContext(ctx); err == nil {
		b.add("host_swap_used_bytes", float64(s.Used), nil)
	}
	if parts, err := disk.PartitionsWithContext(ctx, false); err == nil {
		seen := map[string]bool{}
		for _, p := range parts {
			if seen[p.Device] || !isRealFilesystem(p.Fstype) {
				continue
			}
			seen[p.Device] = true
			if u, err := disk.UsageWithContext(ctx, hostPath(p.Mountpoint)); err == nil {
				labels := map[string]string{"mountpoint": p.Mountpoint}
				b.add("host_disk_total_bytes", float64(u.Total), labels)
				b.add("host_disk_used_bytes", float64(u.Used), labels)
			}
		}
	}
	if io, err := disk.IOCountersWithContext(ctx); err == nil {
		for name, c := range io {
			if strings.HasPrefix(name, "loop") || strings.HasPrefix(name, "ram") {
				continue
			}
			labels := map[string]string{"device": name}
			b.add("host_disk_read_bytes_total", float64(c.ReadBytes), labels)
			b.add("host_disk_written_bytes_total", float64(c.WriteBytes), labels)
			b.add("host_disk_io_time_seconds_total", float64(c.IoTime)/1000, labels)
		}
	}
	if counters, err := psnet.IOCountersWithContext(ctx, true); err == nil {
		for _, c := range counters {
			if c.Name == "lo" || isVirtualInterface(c.Name) {
				continue
			}
			labels := map[string]string{"interface": c.Name}
			b.add("host_network_receive_bytes_total", float64(c.BytesRecv), labels)
			b.add("host_network_transmit_bytes_total", float64(c.BytesSent), labels)
			b.add("host_network_receive_errors_total", float64(c.Errin), labels)
			b.add("host_network_transmit_errors_total", float64(c.Errout), labels)
			b.add("host_network_receive_drop_total", float64(c.Dropin), labels)
			b.add("host_network_transmit_drop_total", float64(c.Dropout), labels)
		}
	}
	return b.samples
}

// hostPath — путь на хосте с учётом HOST_ROOT (агент в контейнере видит корень
// хоста смонтированным, например, в /host/root).
func hostPath(p string) string {
	if root := os.Getenv("HOST_ROOT"); root != "" {
		return filepath.Join(root, p)
	}
	return p
}

func isVirtualInterface(name string) bool {
	for _, prefix := range []string{"veth", "docker", "br-", "cni", "flannel", "virbr", "vxlan"} {
		if strings.HasPrefix(name, prefix) {
			return true
		}
	}
	return false
}

func isRealFilesystem(fstype string) bool {
	switch fstype {
	case "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "vfat", "ntfs":
		return true
	}
	return false
}
